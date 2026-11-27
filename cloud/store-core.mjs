import { createHash, randomBytes } from 'node:crypto';

export const token = () => randomBytes(32).toString('base64url');
export const digest = value => createHash('sha256').update(value).digest('hex');
export function dayOf(ms, zone) {
  const p = Object.fromEntries(new Intl.DateTimeFormat('en', {timeZone: zone, year:'numeric', month:'2-digit', day:'2-digit'}).formatToParts(ms).map(p => [p.type,p.value]));
  return `${p.year}-${p.month}-${p.day}`;
}
const plusDays = (day,n) => new Date(Date.parse(day) + n * 86400000).toISOString().slice(0,10);
export class Fault extends Error {
  constructor(status, code) { super(code); this.status = status; }
}

export const schema = `
    CREATE TABLE IF NOT EXISTS account (only INTEGER PRIMARY KEY CHECK(only=1), id TEXT NOT NULL, salt TEXT NOT NULL, password TEXT NOT NULL, zone TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS auth (hash TEXT PRIMARY KEY, kind TEXT NOT NULL, until INTEGER NOT NULL, label TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS cards (key TEXT PRIMARY KEY, revision INTEGER NOT NULL, status TEXT NOT NULL, hash TEXT NOT NULL, data TEXT NOT NULL, due TEXT, streak INTEGER NOT NULL DEFAULT 0, last TEXT);
    CREATE TABLE IF NOT EXISTS audio (key TEXT NOT NULL REFERENCES cards(key), id TEXT NOT NULL, digest TEXT NOT NULL, bytes BLOB NOT NULL, PRIMARY KEY(key,id));
    CREATE TABLE IF NOT EXISTS tickets (id TEXT PRIMARY KEY, day TEXT NOT NULL, key TEXT NOT NULL REFERENCES cards(key), revision INTEGER NOT NULL, example TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS roster (day TEXT NOT NULL, key TEXT NOT NULL REFERENCES cards(key), slot INTEGER NOT NULL, ticket TEXT NOT NULL REFERENCES tickets(id), PRIMARY KEY(day,key));
    CREATE TABLE IF NOT EXISTS scores (event TEXT PRIMARY KEY, ticket TEXT NOT NULL REFERENCES tickets(id), day TEXT NOT NULL, key TEXT NOT NULL REFERENCES cards(key), example TEXT NOT NULL, outcome TEXT NOT NULL, UNIQUE(day,key));
    CREATE TABLE IF NOT EXISTS receipts (event TEXT PRIMARY KEY, ticket TEXT NOT NULL, outcome TEXT NOT NULL, result TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS scores_examples ON scores(key,example);`;

export function createStore({get,all,run,txn}) {
  const account = () => get('SELECT * FROM account');
  function initAccount({id,salt,password,zone}) {
    dayOf(0,zone);
    run('INSERT INTO account VALUES(1,?,?,?,?)',id,salt,password,zone);
  }
  function grant(kind,until,label,now) {
    const value = token();
    run('DELETE FROM auth WHERE until<=?',now);
    run('INSERT INTO auth VALUES(?,?,?,?)',digest(value),kind,until,label);
    return value;
  }
  const auth = (kind,value,now) => typeof value === 'string' && /^[\w-]{43}$/.test(value)
    ? get('SELECT * FROM auth WHERE hash=? AND kind=? AND until>?',digest(value),kind,now) : undefined;
  function pair(value,now) {
    return txn(() => {
      const code = auth('pair',value,now);
      if (!code) throw new Fault(401,'invalid-pair');
      run('DELETE FROM auth WHERE hash=?',code.hash);
      return grant('upload',Number.MAX_SAFE_INTEGER,code.label,now);
    });
  }
  const keyFor = data => `${data.libraryId}/${data.cardId}`;
  function version(data,hash) {
    const row = get('SELECT revision,hash FROM cards WHERE key=?',keyFor(data));
    if (!row || row.revision < data.revision) return null;
    if (row.revision > data.revision) return {result:'superseded',revision:row.revision};
    if (row.hash !== hash) throw new Fault(409,'revision-conflict');
    return {result:'ready',revision:row.revision};
  }
  function publish(data,media,hash) {
    return txn(() => {
      const existing = version(data,hash);
      if (existing) return existing;
      const key = keyFor(data);
      run(`INSERT INTO cards(key,revision,status,hash,data) VALUES(?,?,?,?,?)
        ON CONFLICT(key) DO UPDATE SET revision=excluded.revision,status=excluded.status,hash=excluded.hash,data=excluded.data`,
        key,data.revision,data.status,hash,JSON.stringify(data));
      run('DELETE FROM audio WHERE key=?',key);
      for (const {id,sha256,bytes} of media) run('INSERT INTO audio VALUES(?,?,?,?)',key,id,sha256,bytes);
      return {result:'ready',revision:data.revision};
    });
  }

  function today(day) {
    return txn(() => {
      const slots = all(`SELECT r.*,t.revision,t.example,c.status,c.revision AS current,c.data,s.event
        FROM roster r JOIN tickets t ON t.id=r.ticket JOIN cards c ON r.key=c.key
        LEFT JOIN scores s ON s.day=r.day AND s.key=r.key WHERE r.day=? ORDER BY r.slot`,day);
      function issue(row) {
        const card = JSON.parse(row.data);
        const stats = new Map(all('SELECT example,count(*) AS n,max(day) AS last FROM scores WHERE key=? GROUP BY example',row.key).map(r => [r.example,r]));
        const e = [...card.examples].sort((a,b) => (stats.get(a.exampleId)?.n ?? 0) - (stats.get(b.exampleId)?.n ?? 0)
          || (stats.get(a.exampleId)?.last ?? '').localeCompare(stats.get(b.exampleId)?.last ?? ''))[0];
        const id = token();
        run('INSERT INTO tickets VALUES(?,?,?,?,?)',id,day,row.key,row.current,e.exampleId);
        return {card,e,id};
      }
      const question = ({card,e,id}) => ({id,expression:card.expression,meaning:card.meaning,english:e.english,chinese:e.chinese,
        source:e.source,sha256:e.sha256,audio:`/api/audio/${id}`});
      const items = [];
      let answered = 0, occupied = 0;
      for (const r of slots) {
        if (r.event) { answered++; occupied++; continue; }
        if (r.status !== 'active') { run('DELETE FROM roster WHERE day=? AND key=?',day,r.key); continue; }
        occupied++;
        if (r.revision !== r.current) {
          const q = issue(r);
          run('UPDATE roster SET ticket=? WHERE day=? AND key=?',q.id,day,r.key);
          items.push(question(q));
        } else {
          const card = JSON.parse(r.data);
          items.push(question({card,e:card.examples.find(e => e.exampleId === r.example),id:r.ticket}));
        }
      }
      const candidates = all(`SELECT key,revision AS current,data FROM cards c WHERE status='active' AND (due IS NULL OR due<=?)
        AND NOT EXISTS(SELECT 1 FROM roster WHERE day=? AND key=c.key)
        ORDER BY due IS NULL,due,key LIMIT ?`,day,day,10-occupied);
      let nextSlot = slots.reduce((n,r) => Math.max(n,r.slot),0);
      for (const r of candidates) {
        const q = issue(r);
        run('INSERT INTO roster VALUES(?,?,?,?)',day,r.key,++nextSlot,q.id);
        items.push(question(q));
      }
      return {day,answered,items};
    });
  }

  function score(event,ticket,outcome,currentDay) {
    return txn(() => {
      const old = get('SELECT * FROM receipts WHERE event=?',event);
      if (old) {
        if (old.ticket !== ticket || old.outcome !== outcome) throw new Fault(409,'event-conflict');
        return JSON.parse(old.result);
      }
      const t = get(`SELECT t.*,c.status,c.revision AS current,c.due,c.streak,c.last FROM tickets t JOIN cards c ON t.key=c.key WHERE t.id=?`,ticket);
      let result;
      if (!t || t.day > currentDay) result = {result:'invalid-ticket'};
      else if (t.status !== 'active') result = {result:'withdrawn',day:t.day};
      else if (t.revision !== t.current) result = {result:'changed',day:t.day};
      else if (get('SELECT event FROM scores WHERE day=? AND key=?',t.day,t.key)) result = {result:'already-scored',day:t.day};
      else {
        run('INSERT INTO scores VALUES(?,?,?,?,?,?)',event,ticket,t.day,t.key,t.example,outcome);
        let due = t.due;
        if (!t.last || t.day > t.last) {
          const streak = outcome === 'heard' ? Math.min(t.streak+1,3) : 0;
          due = plusDays(t.day,[1,1,3,7][streak]);
          run('UPDATE cards SET due=?,streak=?,last=? WHERE key=?',due,streak,t.day,t.key);
        }
        result = {result:'accepted',day:t.day,due};
      }
      run('INSERT INTO receipts VALUES(?,?,?,?)',event,ticket,outcome,JSON.stringify(result));
      return result;
    });
  }
  const audio = id => get(`SELECT a.bytes,a.digest FROM tickets t JOIN cards c ON t.key=c.key JOIN audio a ON a.key=t.key AND a.id=t.example
    WHERE t.id=? AND c.status='active' AND c.revision=t.revision`,id);
  const library = () => all("SELECT data,due FROM cards WHERE status IN ('active','text') ORDER BY key").map(r => {
    const c = JSON.parse(r.data);
    return {expression:c.expression,meaning:c.meaning,count:c.count,due:r.due,textOnly:c.status === 'text',english:c.english,chinese:c.chinese};
  });
  return {account,initAccount,grant,auth,pair,version,publish,today,score,audio,library,
    revoke:hash => run('DELETE FROM auth WHERE hash=?',hash),
    revokeDevice:label => run("DELETE FROM auth WHERE kind IN ('upload','pair') AND label=?",label)};
}
