/* Coral Coast - Google Calendar (iCal) reader.
   The feed comes from Coral Launcher (http://127.0.0.1:47613/calendar), which downloads the
   "secret address in iCal format" set in its config.json. This file only parses and lays out events. */

const CoralCal = (() => {
  const DAY = 864e5;
  const WD = ['SU', 'MO', 'TU', 'WE', 'TH', 'FR', 'SA'];

  function unesc(s) {
    return String(s || '').replace(/\\n/gi, ' ').replace(/\\,/g, ',').replace(/\\;/g, ';').replace(/\\\\/g, '\\').trim();
  }

  // 20261014 (all day) | 20261014T190000Z (UTC) | 20261014T190000 (local / TZID - treated as this PC's time zone)
  function icsDate(v) {
    const m = /^(\d{4})(\d{2})(\d{2})(?:T(\d{2})(\d{2})(\d{2})(Z)?)?$/.exec(String(v).trim());
    if (!m) return null;
    const [, y, mo, d, h, mi, s, z] = m;
    if (h === undefined) return new Date(+y, +mo - 1, +d);
    if (z) return new Date(Date.UTC(+y, +mo - 1, +d, +h, +mi, +s));
    return new Date(+y, +mo - 1, +d, +h, +mi, +s);
  }

  function parse(text) {
    const lines = String(text).replace(/\r?\n[ \t]/g, '').split(/\r?\n/);
    const events = [];
    let cur = null, cal = -1;   // several feeds can arrive back to back; number them 0, 1, 2...
    for (const line of lines) {
      if (line === 'BEGIN:VCALENDAR') { cal++; continue; }
      if (line === 'BEGIN:VEVENT') { cur = { exdates: [], cal: Math.max(cal, 0) }; continue; }
      if (line === 'END:VEVENT') { if (cur) events.push(cur); cur = null; continue; }
      if (!cur) continue;
      const i = line.indexOf(':');
      if (i < 0) continue;
      const left = line.slice(0, i), val = line.slice(i + 1);
      const parts = left.split(';'), name = parts[0].toUpperCase();
      const params = {};
      for (const p of parts.slice(1)) { const j = p.indexOf('='); if (j > 0) params[p.slice(0, j).toUpperCase()] = p.slice(j + 1); }
      if (name === 'DTSTART') { cur.start = icsDate(val); cur.allDay = params.VALUE === 'DATE' || /^\d{8}$/.test(val.trim()); }
      else if (name === 'DTEND') cur.end = icsDate(val);
      else if (name === 'SUMMARY') cur.summary = unesc(val);
      else if (name === 'UID') cur.uid = val.trim();
      else if (name === 'STATUS') cur.status = val.trim().toUpperCase();
      else if (name === 'RRULE') {
        cur.rrule = {};
        for (const kv of val.split(';')) { const j = kv.indexOf('='); if (j > 0) cur.rrule[kv.slice(0, j).toUpperCase()] = kv.slice(j + 1); }
      }
      else if (name === 'EXDATE') { for (const v of val.split(',')) { const d = icsDate(v); if (d) cur.exdates.push(d.getTime()); } }
      else if (name === 'RECURRENCE-ID') { const d = icsDate(val); if (d) cur.recurrenceId = d.getTime(); }
    }
    return events;
  }

  // copy of `base` (keeps its wall-clock time) moved to year/month/day
  function at(base, y, m, d) { return new Date(y, m, d, base.getHours(), base.getMinutes(), base.getSeconds()); }

  // occurrences of a repeating event that start before `to`
  function expand(e, from, to) {
    const r = e.rrule, s0 = e.start, out = [];
    const interval = Math.max(1, parseInt(r.INTERVAL || '1', 10));
    const count = r.COUNT ? parseInt(r.COUNT, 10) : Infinity;
    const untilD = r.UNTIL ? icsDate(r.UNTIL) : null;
    const until = untilD ? (e.allDay && /^\d{8}$/.test(r.UNTIL) ? untilD.getTime() + DAY - 1 : untilD.getTime()) : Infinity;
    let n = 0, guard = 0;
    const take = d => {           // false = stop
      const t = d.getTime();
      if (t > until || n >= count || t >= to) return false;
      if (t >= s0.getTime()) { n++; out.push(d); }
      return true;
    };
    const canSkip = count === Infinity;
    const freq = (r.FREQ || '').toUpperCase();

    if (freq === 'DAILY') {
      let k = canSkip ? Math.max(0, Math.floor((from - s0) / (interval * DAY)) - 1) : 0;
      while (guard++ < 5000 && take(at(s0, s0.getFullYear(), s0.getMonth(), s0.getDate() + k * interval))) k++;
    }
    else if (freq === 'WEEKLY') {
      const by = (r.BYDAY ? r.BYDAY.split(',') : [WD[s0.getDay()]]).map(x => WD.indexOf(x.slice(-2))).filter(x => x >= 0).sort((a, b) => a - b);
      const week0 = new Date(s0.getFullYear(), s0.getMonth(), s0.getDate() - s0.getDay());
      let w = canSkip ? Math.max(0, Math.floor((from - week0) / (7 * DAY * interval)) - 1) : 0;
      outer: while (guard++ < 3000) {
        for (const wd of by) {
          const d = at(s0, week0.getFullYear(), week0.getMonth(), week0.getDate() + w * 7 * interval + wd);
          if (!take(d)) break outer;
        }
        w++;
      }
    }
    else if (freq === 'MONTHLY') {
      const nth = r.BYDAY ? /^([+-]?\d)?(SU|MO|TU|WE|TH|FR|SA)$/.exec(r.BYDAY.split(',')[0]) : null;
      const monthDay = r.BYMONTHDAY ? parseInt(r.BYMONTHDAY.split(',')[0], 10) : s0.getDate();
      const f = new Date(from), monthsIn = (f.getFullYear() - s0.getFullYear()) * 12 + f.getMonth() - s0.getMonth();
      let k = canSkip ? Math.max(0, Math.floor(monthsIn / interval) - 1) : 0;
      while (guard++ < 1200) {
        const y = s0.getFullYear(), m = s0.getMonth() + k * interval;
        let d;
        if (nth) {
          const want = WD.indexOf(nth[2]), which = parseInt(nth[1] || '1', 10);
          if (which > 0) {
            const first = new Date(y, m, 1);
            d = at(s0, y, m, 1 + ((want - first.getDay() + 7) % 7) + (which - 1) * 7);
          } else {
            const last = new Date(y, m + 1, 0);
            d = at(s0, y, m, last.getDate() - ((last.getDay() - want + 7) % 7) + (which + 1) * 7);
          }
          if (d.getMonth() !== ((m % 12) + 12) % 12) { k++; continue; }
        } else {
          const len = new Date(y, m + 1, 0).getDate();
          const day = monthDay < 0 ? len + monthDay + 1 : monthDay;
          if (day > len) { k++; continue; }   // e.g. the 31st in a 30-day month: skipped, as Google does
          d = at(s0, y, m, day);
        }
        if (!take(d)) break;
        k++;
      }
    }
    else if (freq === 'YEARLY') {
      let k = canSkip ? Math.max(0, Math.floor((new Date(from).getFullYear() - s0.getFullYear()) / interval) - 1) : 0;
      while (guard++ < 300) {
        const d = at(s0, s0.getFullYear() + k * interval, s0.getMonth(), s0.getDate());
        if (d.getDate() !== s0.getDate()) { k++; continue; }  // Feb 29 in a non-leap year
        if (!take(d)) break;
        k++;
      }
    }
    else out.push(s0);
    return out;
  }

  // events overlapping [from, to), sorted, as {start, end, allDay, summary}
  function between(text, from, to) {
    const events = parse(text);
    const moved = new Set(events.filter(e => e.recurrenceId != null).map(e => e.cal + '|' + e.uid + '|' + e.recurrenceId));
    const out = [];
    for (const e of events) {
      if (!e.start || e.status === 'CANCELLED') continue;
      const dur = e.end ? Math.max(0, e.end - e.start) : (e.allDay ? DAY : 0);
      const starts = (e.rrule && e.recurrenceId == null) ? expand(e, from - dur, to) : [e.start];
      for (const s of starts) {
        const t = s.getTime();
        if (e.rrule && e.recurrenceId == null && (e.exdates.includes(t) || moved.has(e.cal + '|' + e.uid + '|' + t))) continue;
        if (t < to && t + Math.max(dur, 1) > from) out.push({ start: s, end: new Date(t + dur), allDay: !!e.allDay, summary: e.summary || '(no title)', cal: e.cal });
      }
    }
    return out.sort((a, b) => a.start - b.start || (b.allDay - a.allDay));
  }

  return { parse, between, icsDate };
})();

if (typeof module !== 'undefined') module.exports = CoralCal;
