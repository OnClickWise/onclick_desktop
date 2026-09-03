/**
 * WiseAcademy — backend local do posto (modo desktop / offline).
 *
 * Arranca a MESMA API NestJS contra uma base de dados Postgres embutida
 * (PGlite/WASM, exposta por socket), correndo as migrações da API. Zero
 * alterações ao código da API — só variáveis de ambiente.
 *
 *   node server.mjs --api-dir <path> --data-dir <path> \
 *     --pg-port 55432 --api-port 4310 \
 *     --tenant-id <uuid> --tenant-slug <slug> --tenant-name <name> \
 *     --user-b64 <base64(JSON de data.user)> [--jwt-secret <s>]
 *
 * Escreve "READY <apiUrl>" no stdout quando a API responde. SIGINT/SIGTERM
 * encerra tudo de forma limpa.
 */
import { PGlite } from '@electric-sql/pglite';
import { pgcrypto } from '@electric-sql/pglite/contrib/pgcrypto';
import { PGLiteSocketServer } from '@electric-sql/pglite-socket';
import knexLib from 'knex';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

/* ── args ───────────────────────────────────────────────────────── */
const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, arr) => {
    if (a.startsWith('--')) acc.push([a.slice(2), arr[i + 1]?.startsWith('--') ? true : arr[i + 1]]);
    return acc;
  }, []),
);
const API_DIR = path.resolve(args['api-dir'] || path.join(process.cwd(), '..', '..', 'api'));
const DATA_DIR = path.resolve(args['data-dir'] || path.join(process.env.LOCALAPPDATA || process.cwd(), 'WiseAcademy', 'Desktop'));
const PG_PORT = Number(args['pg-port'] || 55432);
const API_PORT = Number(args['api-port'] || 4310);
const JWT_SECRET = args['jwt-secret'] || 'wiseacademy_secret_2024';
const TENANT = {
  id: args['tenant-id'],
  slug: args['tenant-slug'],
  name: args['tenant-name'] || args['tenant-slug'],
};
let USER = null;
try { USER = args['user-b64'] ? JSON.parse(Buffer.from(args['user-b64'], 'base64').toString('utf8')) : null; } catch { /* ignore */ }

const PG_DATA = path.join(DATA_DIR, 'pgdata');
const MIGRATIONS = path.join(API_DIR, 'dist', 'src', 'database', 'migrations');
const API_ENTRY = path.join(API_DIR, 'dist', 'src', 'main.js');

const log = (...m) => console.error('[local-backend]', ...m); // stderr — stdout é para o READY

for (const [label, p] of [['migrações', MIGRATIONS], ['API', API_ENTRY]]) {
  if (!fs.existsSync(p)) { log('FALTA', label, '->', p, '(fazer "npm run build" na API)'); process.exit(2); }
}
fs.mkdirSync(PG_DATA, { recursive: true });

/* ── PGlite ─────────────────────────────────────────────────────── */
log('a arrancar PGlite em', PG_DATA);
const db = await PGlite.create({ dataDir: PG_DATA, extensions: { pgcrypto } });
const socket = new PGLiteSocketServer({ db, port: PG_PORT, host: '127.0.0.1' });
await socket.start();
log('Postgres embutido em 127.0.0.1:' + PG_PORT);

let apiProc = null;
let closing = false;
async function shutdown(code = 0) {
  if (closing) return;
  closing = true;
  try { apiProc?.kill(); } catch { /* */ }
  try { await socket.stop(); } catch { /* */ }
  try { await db.close(); } catch { /* */ }
  process.exit(code);
}
process.on('SIGINT', () => shutdown(0));
process.on('SIGTERM', () => shutdown(0));

const knex = knexLib({
  client: 'pg',
  connection: { host: '127.0.0.1', port: PG_PORT, user: 'postgres', password: 'postgres', database: 'postgres' },
  pool: { min: 1, max: 4 },
  migrations: { directory: MIGRATIONS, loadExtensions: ['.js'], tableName: 'knex_migrations' },
});

/* ── migrações + seed do tenant/user ───────────────────────────── */
async function migrateWithSeed() {
  let seeded = false;
  for (;;) {
    const [, applied] = await knex.migrate.up();
    if (!applied || applied.length === 0) break;
    if (!seeded && (await knex.schema.hasTable('tenants'))) {
      if (TENANT.id && !(await knex('tenants').where({ id: TENANT.id }).first('id'))) {
        await knex('tenants').insert({
          id: TENANT.id, name: TENANT.name, slug: TENANT.slug, subdomain: TENANT.slug,
          owner_name: USER?.name || 'posto', owner_email: USER?.email || 'posto@local',
          plan: 'trial', status: 'active',
        }).onConflict('id').ignore();
        log('tenant de arranque:', TENANT.slug);
      }
      seeded = true;
    }
  }
  const last = await knex('knex_migrations').orderBy('id', 'desc').first('name');
  log('migrações OK — última:', last?.name);
}

async function ensureUser() {
  if (!USER?.id || !(await knex.schema.hasTable('users'))) return;
  const exists = await knex('users').where({ id: USER.id }).first('id');
  if (exists) return;
  await knex('users').insert({
    id: USER.id, tenant_id: USER.tenant_id ?? TENANT.id ?? null,
    name: USER.name, email: USER.email,
    role: USER.role || 'admin', system_role: USER.system_role || 'tenant_user',
    is_active: true,
  }).onConflict('id').ignore();
  log('user local semeado:', USER.email);
}

try {
  await migrateWithSeed();
  await ensureUser();
} catch (e) {
  log('FALHA nas migrações:', e.message);
  await shutdown(3);
}
await knex.destroy();

/* ── API NestJS ────────────────────────────────────────────────── */
log('a arrancar a API em :' + API_PORT);
apiProc = spawn(process.execPath, [API_ENTRY], {
  cwd: API_DIR,
  env: {
    ...process.env,
    NODE_ENV: 'production',
    PORT: String(API_PORT),
    JWT_SECRET,
    DATABASE_HOST: '127.0.0.1',
    DATABASE_PORT: String(PG_PORT),
    DATABASE_USER: 'postgres',
    DATABASE_PASSWORD: 'postgres',
    DATABASE_NAME: 'postgres',
    WA_DESKTOP: '1',
  },
  stdio: ['ignore', 'inherit', 'inherit'],
});
apiProc.on("exit", (code) => { log('API terminou (' + code + ')'); shutdown(code ?? 0); });

/* ── espera pela API e sinaliza READY ──────────────────────────── */
const apiUrl = `http://127.0.0.1:${API_PORT}`;
const deadline = Date.now() + 60_000;
while (Date.now() < deadline) {
  try {
    const r = await fetch(apiUrl + '/', { signal: AbortSignal.timeout(2000) });
    if (r.status) { process.stdout.write('READY ' + apiUrl + '\n'); log('pronto:', apiUrl); break; }
  } catch { /* ainda não */ }
  await new Promise((r) => setTimeout(r, 500));
}

/* mantém o processo vivo (a API é filha; SIGINT/SIGTERM já encerram tudo) */

