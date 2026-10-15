// A stub of the gateway for tests/runner/fwrunner.test.sh: the three calls the fwrunner image makes (runtime/redeem, plugin,
// verbs/git-credential), by the contract of docs/design/cloud-worker-runtimes.md 2.2 and 2.6. Credential-like values are composed at run time.
//   node stub-gateway.js <port-file> <plugin.tgz>
const http = require('http'), fs = require('fs');
const [portFile, pluginFile] = process.argv.slice(2);
const nonce = 'nonce-' + 'x'.repeat(8);
const ghs = 'ghs_' + 'A'.repeat(36);
const i1 = 'i1.' + '42.owner.' + 'B'.repeat(20);
let spent = false, workerToken = '', stats = { redeem: 0, plugin: 0, credential: 0 };
const send = (res, code, obj) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(obj)); };
http.createServer((req, res) => {
  let body = '';
  req.on('data', d => body += d).on('end', () => {
    const auth = (req.headers.authorization || '').replace(/^Bearer /, '');
    if (req.method === 'POST' && req.url === '/api/v1/runtime/redeem') {
      stats.redeem++;
      let b = {}; try { b = JSON.parse(body); } catch { return send(res, 400, { ok: false, code: 'bad-json' }); }
      if (b.nonce !== nonce) return send(res, 403, { ok: false, code: 'bad-nonce' });
      if (spent) return send(res, 410, { ok: false, code: 'nonce-spent' });
      spent = true;
      workerToken = 'w2.' + b.name + '.cae-job.' + (Math.floor(Date.now() / 1000) + 3600) + '.' + 'C'.repeat(43);
      return send(res, 200, { ok: true, name: b.name, token: workerToken });
    }
    if (req.method === 'GET' && req.url === '/api/v1/plugin') {
      stats.plugin++;
      if (!workerToken || auth !== workerToken) return send(res, 401, { ok: false, code: 'unauthorized' });
      res.writeHead(200, { 'Content-Type': 'application/gzip' }); return res.end(fs.readFileSync(pluginFile));
    }
    if (req.method === 'POST' && req.url === '/api/v1/verbs/git-credential') {
      stats.credential++;
      if (auth !== i1) return send(res, 403, { ok: false, code: 'forbidden' });
      let b = {}; try { b = JSON.parse(body); } catch { }
      if (b.item !== 42) return send(res, 403, { ok: false, code: 'not-your-item' });
      return send(res, 200, { ok: true, token: ghs, expires_at: new Date(Date.now() + 3600e3).toISOString() });
    }
    if (req.url === '/_stub/stats') return send(res, 200, stats);
    if (req.url === '/_stub/secrets') return send(res, 200, { nonce, ghs, i1, workerToken });
    send(res, 404, { ok: false });
  });
}).listen(0, '127.0.0.1', function () { fs.writeFileSync(portFile, String(this.address().port)); });
