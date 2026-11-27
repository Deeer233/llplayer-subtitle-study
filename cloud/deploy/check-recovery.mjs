import { spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { isAbsolute, join } from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { pathToFileURL } from 'node:url';

const [app, ffmpeg] = process.argv.slice(2);
let stage = 'preflight';
let directory;
function check(ok, message) {
  if (!ok) throw new Error(message);
}

async function run() {
  check(app && ffmpeg && isAbsolute(app) && isAbsolute(ffmpeg), 'Pass absolute paths to the app directory and FFmpeg.');
  check(process.getuid?.() !== 0, 'Run as the unprivileged service user, not root.');
  if (process.platform !== 'win32') process.umask(0o077);
  const { openStore, digest, token } = await import(pathToFileURL(join(app, 'store.mjs')).href);
  const { makeServer } = await import(pathToFileURL(join(app, 'server.mjs')).href);
  const generated = spawnSync(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-nostdin', '-f', 'lavfi',
    '-i', 'sine=frequency=440:duration=0.45', '-c:a', 'libmp3lame', '-write_xing', '0', '-f', 'mp3', 'pipe:1'],
    { timeout: 15000, maxBuffer: 1024 * 1024 });
  check(generated.status === 0 && generated.stdout?.length > 64, 'Synthetic MP3 generation failed.');
  const mp3 = generated.stdout, sha256 = digest(mp3);
  directory = mkdtempSync(join(tmpdir(), 'private-review-recovery-'));
  const source = join(directory, 'source.sqlite'), backup = join(directory, 'backup.sqlite');
  const restored = join(directory, 'restored.sqlite');
  const password = token(), event = randomUUID(), exampleId = randomUUID();
  const now = Date.now(); // Keep the two instances on the same learning day.
  const payload = { card: { libraryId: randomUUID(), cardId: randomUUID(), revision: 1, status: 'active',
    expression: 'recovery sample', meaning: 'synthetic fixture', count: 1, reason: 'manual',
    examples: [{ exampleId, english: 'This is a recovery sample.', target: 'recovery sample',
      source: 'Synthetic fixture', verified: true, sha256 }] }, media: [{ id: exampleId, mp3: mp3.toString('base64') }] };

  function cli(path, command) {
    const child = spawnSync(process.execPath, [join(app, 'cli.mjs'), command, backup],
      { env: { ...process.env, REVIEW_DB: path }, timeout: 15000 });
    check(child.status === 0, `${command} CLI failed (${child.error?.code ?? child.signal ?? child.status}).`);
  }
  function snapshot(db) {
    return {
      cards: db.prepare('SELECT * FROM cards ORDER BY key').all(),
      scores: db.prepare('SELECT * FROM scores ORDER BY event').all(),
      audio: db.prepare('SELECT key,id,digest,bytes FROM audio ORDER BY key,id').all()
        .map(({bytes, ...row}) => ({...row, contentDigest: digest(bytes)})),
    };
  }
  async function withInstance(path, action) {
    const store = openStore(path);
    let server;
    try {
      // The smoke test sends its loopback origin to login and score requests.
      // The app only requires POST Origin to match the configured origin.
      const origin = 'http://127.0.0.1';
      server = makeServer(store, { origin, now: () => now, ffmpeg });
      server.listen(0, '127.0.0.1');
      await once(server, 'listening');
      const endpoint = `${origin}:${server.address().port}`;
      async function request(label, path, status, { method, data, cookie, bearer, range } = {}) {
        const response = await fetch(endpoint + path, { method: method ?? (data ? 'POST' : 'GET'),
          headers: { Origin: origin, Connection: 'close', ...(data && {'Content-Type': 'application/json'}),
            ...(cookie && {Cookie: cookie}), ...(bearer && {Authorization: `Bearer ${bearer}`}),
            ...(range && {Range: range}) }, body: data && JSON.stringify(data), signal: AbortSignal.timeout(10000) });
        check(response.status === status, `${label}: expected HTTP ${status}, got ${response.status}.`);
        return { cookie: response.headers.get('set-cookie')?.split(';')[0], body:
          response.headers.get('content-type')?.includes('json') ? await response.json() : Buffer.from(await response.arrayBuffer()) };
      }
      const login = async () => {
        const { cookie } = await request('login', '/api/login', 200, { data: { password } });
        check(cookie, 'Login returned no session cookie.');
        return cookie;
      };
      const grant = () => store.grant('pair', now + 600000, 'recovery-check', now);
      const pair = async () => {
        const { body } = await request('pair', '/api/pair', 200, { data: {code: grant()} });
        check(body.uploadToken, 'Pairing returned no upload credential.');
        return body.uploadToken;
      };
      await action({ store, request, login, pair, grant });
    } finally {
      if (server?.listening) await new Promise(resolve => server.close(resolve));
      store.db.close();
    }
  }

  let oldCookie, oldUpload, oldCode, question, score, before, scoreInput;
  stage = 'seed-and-backup';
  await withInstance(source, async ({store, request, login, pair, grant}) => {
    store.init(password, 'Asia/Shanghai');
    oldCookie = await login();
    oldUpload = await pair();
    oldCode = grant();
    const { body: published } = await request('publish', '/api/cards', 200, { method: 'PUT', bearer: oldUpload, data: payload });
    check(published.result === 'ready', 'Synthetic card was not published.');
    const { body: today } = await request('today', '/api/today', 200, { cookie: oldCookie });
    check(today.items.length === 1, 'Expected exactly one playable card.');
    [question] = today.items;
    check(question.sha256 === sha256, 'Ticket MP3 digest mismatch.');
    const { body: audio } = await request('source audio', question.audio, 200, { cookie: oldCookie });
    check(audio.equals(mp3), 'Source MP3 bytes mismatch.');
    scoreInput = { event, ticket: question.id, outcome: 'heard' };
    ({ body: score } = await request('score', '/api/score', 200, { cookie: oldCookie, data: scoreInput }));
    const expectedDue = new Date(Date.parse(today.day) + 86400000).toISOString().slice(0, 10);
    check(score.result === 'accepted' && score.day === today.day && score.due === expectedDue, 'Unexpected learning day or schedule.');
    before = snapshot(store.db);
    check(before.cards.length === 1 && before.cards[0].due === expectedDue && before.scores.length === 1 &&
      before.audio.length === 1 && before.audio[0].digest === sha256 && before.audio[0].contentDigest === sha256,
      'Source snapshot is missing card, audio, score or schedule.');
    cli(source, 'backup');
  });

  stage = 'restore';
  cli(restored, 'restore');
  stage = 'verify-restored-data-and-auth';
  await withInstance(restored, async ({store, request, login, pair}) => {
    check(isDeepStrictEqual(snapshot(store.db), before), 'Restored cards, MP3, scores or due dates differ.');
    await request('old cookie', '/api/library', 401, { cookie: oldCookie });
    await request('old cookie audio', question.audio, 401, { cookie: oldCookie });
    await request('old upload credential', '/api/cards', 401, { method: 'PUT', bearer: oldUpload, data: payload });
    await request('old unused pairing code', '/api/pair', 401, { data: {code: oldCode} });
    const cookie = await login();
    const { body: library } = await request('restored library', '/api/library', 200, {cookie});
    check(library.length === 1 && library[0].due === score.due, 'Restored library or due date differs.');
    const { body: full } = await request('restored audio', question.audio, 200, {cookie});
    const { body: partial } = await request('restored audio range', question.audio, 206, {cookie, range: 'bytes=0-63'});
    check(full.equals(mp3) && partial.equals(mp3.subarray(0, 64)), 'Restored full or ranged audio bytes differ.');
    const { body: replay } = await request('score replay', '/api/score', 200, { cookie, data: scoreInput });
    check(isDeepStrictEqual(replay, score), 'Restored scoring receipt differs.');
    const { body: republish } = await request('newly paired upload', '/api/cards', 200,
      { method: 'PUT', bearer: await pair(), data: payload });
    check(republish.result === 'ready' && isDeepStrictEqual(snapshot(store.db), before), 'A retry changed restored data.');
  });
  console.log(`PRIVATE_REVIEW_ISOLATED_RECOVERY_OK cards=1 audio=1 scores=1 day=${score.day} due=${score.due} sha256=${sha256}`);
}

try {
  await run();
} catch (error) {
  console.error(`Recovery check failed at ${stage}: ${error.message}`);
  process.exitCode = 1;
} finally {
  if (directory) console.log(`Synthetic files retained at: ${directory}`);
}
