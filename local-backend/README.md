# local-backend — pilha local do posto (Modo Desktop)

Arranca, em processos Node, a **mesma** API NestJS e o **mesmo** frontend Next.js
do WiseAcademy, mas **100% locais** e sem internet:

```
server.mjs
 ├─ PGlite (Postgres 16 em WASM)  ── socket 127.0.0.1:55432
 ├─ migrações da API (dist/src/database/migrations) + seed do tenant/user da sessão
 └─ API NestJS (dist/src/main.js) ── 127.0.0.1:4310   (env DATABASE_*, JWT_SECRET, WA_DESKTOP=1)
```

O executável do posto (`WiseAcademy.Desktop`) também sobe o frontend `standalone`
(`wiseacademy_front/.next/standalone/server.js`) em `127.0.0.1:4311` e aponta o
WebView2 para lá, injetando `window.__WA_API_URL = http://127.0.0.1:4310` para o
frontend falar com a API local (ver `src/lib/api.ts`).

## Correr à mão (dev)

```bash
# 1. compilar a API e o frontend uma vez
(cd ../../api && npm run build)
(cd ../../wiseacademy_front && npm run build)

# 2. instalar deps deste utilitário
npm install

# 3. arrancar a pilha local
node server.mjs \
  --api-dir ../../api \
  --tenant-id <uuid> --tenant-slug wiseacademy-demo --tenant-name "WiseAcademy Demo" \
  --user-b64 $(node -e "process.stdout.write(Buffer.from(JSON.stringify({id:'...',name:'...',email:'...',role:'admin',tenant_id:'<uuid>'})).toString('base64'))")
# imprime  READY http://127.0.0.1:4310  quando a API responde
```

## Dois modos

| Modo    | Frontend                    | API                      | Internet |
|---------|-----------------------------|--------------------------|----------|
| Web     | servidor central            | servidor central         | precisa  |
| Desktop | `.next/standalone` local    | NestJS local + PGlite    | dispensa |

Sem ligação, o posto muda sozinho para **Modo Desktop**. A sincronização
com o central (change-log bidirecional) entra a seguir, domínio a domínio.

## Empacotamento (instalador)

O instalador deve juntar, em `stack/` ao lado do executável:
`runtime/node/` (Node ≥ 20), `stack/api/` (`dist` + `node_modules`),
`stack/local-backend/` (este `server.mjs` + `node_modules`) e
`stack/frontend/` (`.next/standalone` + `.next/static` + `public`).
`LocalStack.cs` deteta estes caminhos automaticamente.
