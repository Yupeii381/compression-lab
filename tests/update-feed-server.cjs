'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(process.argv[2]);
const port = Number(process.argv[3]);
http.createServer((request, response) => {
    try {
        const config = JSON.parse(fs.readFileSync(path.join(root, 'server-config.json'), 'utf8').replace(/^\uFEFF/, ''));
        if (request.url === '/feed.json') {
            const body = JSON.stringify(config.manifest);
            response.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) });
            response.end(body);
        } else if (request.url === '/package.zip') {
            const size = fs.statSync(config.package).size;
            response.writeHead(200, { 'Content-Type': 'application/zip', 'Content-Length': size });
            fs.createReadStream(config.package).pipe(response);
        } else {
            response.writeHead(404).end();
        }
    } catch (error) {
        response.writeHead(500).end(error.message);
    }
}).listen(port, '127.0.0.1', () => console.log(`Test feed: http://127.0.0.1:${port}`));
