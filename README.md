# WiseAcademy — Posto (Desktop)

Aplicação **Windows (WinForms, .NET 9)** para os postos de trabalho da instituição.
Funciona **offline** e **sincroniza automaticamente** com o sistema web quando a
ligação volta.

## O que faz

| Estado | Ecrã | Comportamento |
|---|---|---|
| **Com ligação** | Aplicação web completa (WebView2) | Carrega o frontend web (`WebAppUrl`) já autenticado com a sessão local — o mesmo sistema do browser, dentro do posto. |
| **Sem ligação** | Painel nativo de registo de ponto | Botões Entrada / Saída p/ almoço / Retorno / Saída (fim jornada). As marcações vão para uma **fila local** (SQLite). |
| **Ligação volta** | — | O motor de sincronização envia as marcações pendentes para `POST /rh/ponto/bater` (com o instante original) e recarrega a app web. |

A barra superior mostra sempre: **● Online/Offline**, alternador *Aplicação web / Posto offline*,
**Sincronizar agora**, contador *por sincronizar / erros*, utilizador e **Terminar sessão**.

## Arquitetura

```
WiseAcademy.Desktop.sln
├─ src/WiseAcademy.Desktop.Core   (lógica, testável — net9.0-windows)
│   ├─ Models.cs            AppConfig, Session, LocalPunch, PunchDay, SyncSnapshot
│   ├─ LocalStore.cs        SQLite (%LOCALAPPDATA%\WiseAcademy\Desktop\local.db)
│   │                        · sessão (token cifrado com DPAPI/utilizador)
│   │                        · outbox_punch (fila de marcações — padrão outbox)
│   ├─ ApiClient.cs         cliente HTTP do backend WiseAcademy; distingue
│   │                        offline (rede) de erro da API (validação / 401)
│   ├─ ConnectivityMonitor  sonda periódica à rede + servidor; evento StatusChanged
│   └─ SyncService.cs       drena a fila ao ficar online; auto-corre quando a rede volta
├─ src/WiseAcademy.Desktop        (WinForms — net9.0-windows)
│   ├─ Program.cs           arranque: config → store → login (se preciso) → shell
│   ├─ LoginForm.cs         autenticação nativa; permite "continuar offline"
│   ├─ MainForm.cs          casca: WebView2 (web completa) + painel offline + barra
│   ├─ OfflinePunchPanel.cs painel nativo de ponto (relógio, botões, cartão, fila)
│   └─ appsettings.json     ApiBaseUrl, WebAppUrl, TenantSlug, Source="desktop"…
└─ tests/WiseAcademy.Desktop.SmokeTest   verificação ponta-a-ponta do modo offline
```

## Configuração — `src/WiseAcademy.Desktop/appsettings.json`

```json
{
  "ApiBaseUrl": "https://api.suaescola.pt",     // backend (NestJS)
  "WebAppUrl":  "https://app.suaescola.pt",     // frontend web (Next)
  "TenantSlug": "sua-escola",
  "Source": "desktop",
  "ConnectivityProbeSeconds": 15,
  "MaxSyncAttempts": 8
}
```

O backend aceita `source: "desktop"` no registo de ponto (enum `ATTENDANCE_SOURCES`).

## Executar / compilar

```bash
cd desktop
dotnet build -c Release
# executável: src/WiseAcademy.Desktop/bin/Release/net9.0-windows/WiseAcademy.Desktop.exe
```

Requisitos: **.NET 9 SDK** (ou runtime Windows Desktop) e o **WebView2 Runtime**
(já vem no Windows 10/11 com o Edge; se faltar, a app abre só no painel offline).

## Instalador (Inno Setup)

```bash
cd desktop
dotnet publish src/WiseAcademy.Desktop/WiseAcademy.Desktop.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=false -p:DebugType=none \
  -p:SatelliteResourceLanguages=en -o publish

"C:\Program Files\Inno Setup 7\ISCC.exe" installer\WiseAcademyPosto.iss
```

Gera **`installer/Output/WiseAcademyPosto-Setup-<ver>.exe`** (self-contained, não precisa
do .NET no destino). Depois copia-o para a API que o serve, junto com um `manifest.json`
atualizado (versão / sha256 / tamanho):

```
copy installer\Output\WiseAcademyPosto-Setup-1.0.0.exe  <api>\public\downloads\desktop\
```

### Download pela API

| Rota | Resposta |
|---|---|
| `GET /downloads/desktop` | `302` → `.exe` da última versão |
| `GET /downloads/desktop/manifest.json` | `{ version, filename, size, sha256, path, published_at, notes }` |
| `GET /downloads/desktop/<ficheiro>.exe` | stream, `Content-Disposition: attachment` |

Sem autenticação (é um instalador). Ficheiros em `api/public/downloads/desktop/`.

## Verificação

```bash
# com a API a correr em http://localhost:3002
dotnet run --project tests/WiseAcademy.Desktop.SmokeTest -- <email> <pass> http://localhost:3002 <slug>
```

Cobre: fila local, login + persistência da sessão, drenagem online, deteção
offline e sincronização ao voltar a rede.
