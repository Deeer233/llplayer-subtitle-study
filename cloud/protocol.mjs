import { Buffer } from 'node:buffer';
import { digest, Fault } from './store-core.mjs';

export const ID = /^[a-f0-9-]{36}$/i, TOKEN = /^[\w-]{43}$/, SHA = /^[a-f0-9]{64}$/;
export const valid = (ok,code='invalid-data') => { if (!ok) throw new Fault(422,code); };
const text = (value,max,required=true) => {
  valid(typeof value === 'string' && value.length <= max && (!required || value.trim().length) && !/[\u0000-\u0008\u000b-\u001f]/u.test(value));
  return value.trim();
};
export const object = input => input && typeof input === 'object' && !Array.isArray(input);
export function decodeCard(input) {
  valid(object(input) && input.card && Array.isArray(input.media));
  const c = input.card;
  valid(typeof c.libraryId === 'string' && ID.test(c.libraryId) && typeof c.cardId === 'string' && ID.test(c.cardId)
    && Number.isSafeInteger(c.revision) && c.revision > 0 && ['active','withdrawn','text'].includes(c.status)
    && Number.isSafeInteger(c.count) && c.count >= 0 && ['auto','manual','import'].includes(c.reason) && Array.isArray(c.examples));
  const card = {libraryId:c.libraryId.toLowerCase(), cardId:c.cardId.toLowerCase(), revision:c.revision,
    status:c.status, expression:text(c.expression,120), meaning:text(c.meaning,400,c.reason !== 'import'), count:c.count,reason:c.reason,examples:[]};
  if (c.status === 'text') {
    valid(c.reason === 'import' && c.count === 0 && !c.examples.length && !input.media.length);
    card.english = text(c.english ?? '',1000,false);
    card.chinese = text(c.chinese ?? '',1000,false);
    return {card,media:[]};
  }
  valid(c.reason !== 'import');
  if (c.status === 'withdrawn') { valid(!c.examples.length && !input.media.length); return {card,media:[]}; }
  valid(c.examples.length >= 1 && c.examples.length <= 8 && input.media.length === c.examples.length
    && c.count >= (c.reason === 'auto' ? 2 : 1), 'incomplete-card');
  const ids = new Set(), media = [];
  for (const e of c.examples) {
    valid(e && typeof e.exampleId === 'string' && ID.test(e.exampleId) && !ids.has(e.exampleId.toLowerCase()) && e.verified === true
      && typeof e.sha256 === 'string' && SHA.test(e.sha256));
    const exampleId = e.exampleId.toLowerCase();
    ids.add(exampleId);
    const source = text(e.source,160);
    valid(!/[\\/]|[a-z]:/i.test(source),'invalid-source');
    const english = text(e.english,1000), target = text(e.target,120);
    valid(english.toLowerCase().includes(target.toLowerCase()),'target-missing');
    card.examples.push({exampleId,english,target,chinese:text(e.chinese ?? '',1000,false),source,verified:true,sha256:e.sha256});
    const m = input.media.find(m => m?.id?.toLowerCase?.() === exampleId);
    valid(m && typeof m.mp3 === 'string' && m.mp3.length <= 800000 && /^[A-Za-z0-9+/]+={0,2}$/.test(m.mp3),'missing-audio');
    const bytes = Buffer.from(m.mp3,'base64');
    valid(bytes.length >= 128 && digest(bytes) === e.sha256,'audio-digest');
    media.push({id:exampleId,sha256:e.sha256,bytes});
  }
  return {card,media};
}
