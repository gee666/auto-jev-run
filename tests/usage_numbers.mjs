import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const html = fs.readFileSync(new URL('../src/AutoJev.Local/usage.html', import.meta.url), 'utf8');
const helpers = html.split('<script>')[1].split('let offset=')[0];
const { compact, number } = vm.runInNewContext(helpers + '\n({compact,number})');
for (const [input, expected] of [
  [null, '\u2014'], [undefined, '\u2014'], [0, '0'], [999, '999'],
  [1000, '1k'], [12000, '12k'], [12345, '12.3k'],
  [22000000, '22m'], [90206925, '90.2m'], [1230000000, '1.23b'],
  [999999, '1m'], [999999999, '1b'], [-12300, '-12.3k'],
]) assert.equal(compact(input), expected, String(input));
assert.equal(number(null), '\u2014');
assert.equal(number(90206925), '<span title="90,206,925">90.2m</span>');
assert.ok(html.includes("+' of '+compact(total)"), 'Pagination must use plain text, not HTML');
console.log('PASS: compact counts, unit boundaries, unknown counts and exact-value tooltips.');
