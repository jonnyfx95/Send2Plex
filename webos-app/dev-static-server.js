// Server statico minimo per testare webos-app/ in un browser desktop prima del deploy sulla TV
// vera — stesso pattern di debug già usato nel progetto (vedi docs/piano-webos-app.md). Non fa
// parte del pacchetto WebOS (non referenziato da appinfo.json), solo un comodo per lo sviluppo.
const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = __dirname;
const PORT = process.env.PORT || 8090; // autoPort (launch.json): più sessioni possono avviarlo in parallelo su porte diverse

const MIME = {
  '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css',
  '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml'
};

http.createServer((req, res) => {
  let filePath = path.join(ROOT, decodeURIComponent(req.url.split('?')[0]));
  if (filePath === ROOT || req.url === '/') filePath = path.join(ROOT, 'index.html');
  fs.readFile(filePath, (err, data) => {
    if (err) { res.writeHead(404); res.end('Not found'); return; }
    res.writeHead(200, { 'Content-Type': MIME[path.extname(filePath)] || 'application/octet-stream' });
    res.end(data);
  });
}).listen(PORT, () => console.log(`webos-app dev server su http://localhost:${PORT}`));
