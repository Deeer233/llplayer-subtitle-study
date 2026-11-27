import { mkdirSync, existsSync, copyFileSync, constants } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { DatabaseSync } from 'node:sqlite';
import { openStore } from './store.mjs';
import { makeServer } from './server.mjs';

const [command,arg] = process.argv.slice(2);
if (process.platform !== 'win32') process.umask(0o077);
const path = resolve(process.env.REVIEW_DB ?? fileURLToPath(new URL('./data/review.sqlite',import.meta.url)));
function readSecret(prompt) {
  if (!process.stdin.isTTY) throw new Error('Run init in an interactive terminal.');
  process.stderr.write(prompt);
  return new Promise((resolve,reject) => {
    const input = process.stdin;
    input.setEncoding('utf8'); input.setRawMode(true); input.resume();
    let value = '';
    const finish = () => { input.off('data',data); input.setRawMode(false); input.pause(); process.stderr.write('\n'); };
    function data(chunk) {
      for (const ch of chunk) {
        if (ch === '\r' || ch === '\n') { finish(); resolve(value); return; }
        if (ch === '\u0003') { finish(); reject(new Error('Cancelled.')); return; }
        if (ch === '\b' || ch === '\u007f') value = [...value].slice(0,-1).join('');
        else if (ch >= ' ' && value.length < 257) value += ch;
      }
    }
    input.on('data',data);
  });
}
try {
  mkdirSync(dirname(path),{recursive:true,mode:0o700});
  if (command === 'restore') {
    if (!arg || existsSync(path) || existsSync(path+'-wal') || existsSync(path+'-shm')) throw new Error('Restoration requires a backup and an empty database destination.');
    const backup = resolve(arg);
    if (!existsSync(backup) || existsSync(backup+'-wal')) throw new Error('Use an offline backup created by the backup command.');
    const source = new DatabaseSync(backup,{readOnly:true});
    try {
      if (source.prepare('PRAGMA quick_check').get().quick_check !== 'ok' || !source.prepare('SELECT id FROM account').get()) throw new Error('Invalid backup.');
    } finally { source.close(); }
    copyFileSync(backup,path,constants.COPYFILE_EXCL);
    const restored = openStore(path);
    restored.db.exec('DELETE FROM auth'); restored.db.close();
    console.log('Restored. Sign in again and re-pair the desktop.');
  } else {
    const store = openStore(path);
    if (command === 'init') {
      if (store.account()) throw new Error('The single account already exists.');
      const password = await readSecret('New password (12–256 characters): ');
      if (password !== await readSecret('Confirm password: ')) throw new Error('Passwords do not match.');
      store.init(password,arg ?? 'Asia/Shanghai');
      console.log('Private account created.');
    } else if (command === 'serve') {
      const port = Number(process.env.REVIEW_PORT ?? '8765');
      const origin = process.env.REVIEW_ORIGIN ?? `http://127.0.0.1:${port}`;
      const server = makeServer(store,{origin,ffmpeg:process.env.REVIEW_FFMPEG});
      server.on('error',e => { console.error(e.code); process.exitCode=1; store.db.close(); });
      server.listen(port,'127.0.0.1',() => console.log(`Local listener: 127.0.0.1:${port}; site: ${origin}${store.account()?'':' (account not initialized)'}`));
    } else if (command === 'pair' || command === 'revoke') {
      if (!store.account() || !arg || !/^[\p{L}\p{N}_ -]{1,40}$/u.test(arg)) throw new Error('Initialize the account and provide a device label (1–40 letters/numbers/spaces).');
      const time = Date.now();
      if (command === 'pair') process.stdout.write(store.grant('pair',time+10*60000,arg,time));
      else console.log(`Revoked: ${store.revokeDevice(arg).changes}`);
    } else if (command === 'backup') {
      if (!arg || !store.account()) throw new Error('Initialize the account and provide a new destination.');
      const dest = resolve(arg);
      if (existsSync(dest)) throw new Error('Backup already exists.');
      store.db.prepare('VACUUM INTO ?').run(dest);
      console.log('Backup complete. Keep it private.');
    } else throw new Error('Commands: init [timezone] | serve | pair DEVICE | revoke DEVICE | backup NEW_FILE | restore BACKUP');
    if (command !== 'serve') store.db.close();
  }
} catch (error) { console.error(error.message); process.exitCode=1; }
