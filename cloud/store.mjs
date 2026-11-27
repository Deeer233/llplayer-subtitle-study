import { DatabaseSync } from 'node:sqlite';
import { randomBytes, scryptSync, timingSafeEqual } from 'node:crypto';
import { createStore, digest, dayOf, Fault, schema, token } from './store-core.mjs';

export { digest, dayOf, Fault, token };

export function openStore(path) {
  const db = new DatabaseSync(path, {timeout:5000});
  db.exec('PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;');
  db.exec(schema);
  const get = (sql,...args) => db.prepare(sql).get(...args);
  const all = (sql,...args) => db.prepare(sql).all(...args);
  const run = (sql,...args) => db.prepare(sql).run(...args);
  function txn(fn) {
    db.exec('BEGIN IMMEDIATE');
    try { const result = fn(); db.exec('COMMIT'); return result; }
    catch (e) { db.exec('ROLLBACK'); throw e; }
  }
  const store = createStore({get,all,run,txn});
  function init(password, zone) {
    if (typeof password !== 'string' || password.length < 12 || password.length > 256) throw new Error('Use a password of 12–256 characters.');
    const salt = randomBytes(16).toString('hex');
    store.initAccount({id:token(),salt,password:scryptSync(password,salt,64).toString('hex'),zone});
  }
  const checkPassword = (a,value) => typeof value === 'string' && value.length <= 256 &&
    timingSafeEqual(scryptSync(value,a.salt,64),Buffer.from(a.password,'hex'));
  return {db,...store,init,checkPassword};
}
