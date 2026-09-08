# AgentStudio

Self-hosted studio do tworzenia, testowania i publikowania agentów AI z wizualnym edytorem workflow.

## Stos techniczny

- .NET 10
- Blazor Web App (Interactive Server)
- ASP.NET Core Minimal API
- Entity Framework Core (PostgreSQL — domyślnie także w dev; InMemory tylko jako fallback bez Dockera)
- Microsoft.Extensions.AI + OpenAI-compatible adapters (Ollama, LM Studio, vLLM)
- Własny edytor grafu SVG (Blazor)

## Struktura

```
AgentStudio/
├── AgentStudio.Domain         — encje, walidator grafu, evaluator warunków
├── AgentStudio.Application    — WorkflowRunner, AgentService, conversation store
├── AgentStudio.Infrastructure — EF Core, repozytoria, SecureHttpExecutor, chat client factory
├── AgentStudio.Contracts      — DTO, GraphMapper
├── AgentStudio.Web            — Blazor studio + REST API + SSE + widget (jedna aplikacja)
└── AgentStudio.Tests          — 113 testów (walidator, runner, RAG, sub-agenci, konektory DB, auth, REST API end-to-end — patrz sekcja Testy)
```

## Uruchomienie (dev)

```bash
cd AgentStudio.Web && dotnet run
```

Jedna aplikacja (studio + REST API + widget) na http://localhost:5251.

- Studio: http://localhost:5251 (lista agentów, /providers)
- REST API: http://localhost:5251/api/...
- Widget: http://localhost:5251/widget/agentstudio.js

**Wymaga Postgresa** (agenci, providerzy, rozmowy, dokumenty — wszystko trwałe, nic w pamięci
procesu). `appsettings.Development.json` wskazuje domyślnie na
`Host=localhost;Port=5433;Database=agentstudio;Username=agentstudio;Password=agentstudio`
(port 5433, nie 5432 — żeby nie kolidować z innym lokalnym Postgresem). Podnieś bazę:

```bash
docker run -d --name agentstudio-postgres \
  -e POSTGRES_DB=agentstudio -e POSTGRES_USER=agentstudio -e POSTGRES_PASSWORD=agentstudio \
  -p 5433:5432 -v agentstudio_pgdata:/var/lib/postgresql/data postgres:17
```

(albo `docker-compose.yml` w repo — wymaga nowszego `docker compose`/`docker-compose`, niż jest
domyślnie na tej maszynie deweloperskiej). Migracje EF Core aplikują się automatycznie przy
starcie. Restart `dotnet run` **nie** usuwa agentów/providerów/rozmów — dane żyją w kontenerze
(nazwany wolumen `agentstudio_pgdata`), przeżywają nawet restart samego kontenera.

`ConnectionStrings:AgentStudio = "InMemory"` w `appsettings.json` (bazowy plik, bez override'u
Development) zostaje jako fallback dla CI/szybkich testów bez Dockera — świadomie ulotny,
dane znikają przy restarcie procesu. Zmień connection string w `appsettings.Production.json`
dla wdrożenia.

## Konfiguracja modeli

1. Otwórz http://localhost:5251/providers
2. Dodaj provider OpenAI-compatible, np.:
   - Name: `ollama`
   - Base URL: `http://localhost:11434`
   - Default model: `llama3.1`
3. Utwórz agenta na http://localhost:5251 wskazując providera i model.

## Workflow

Typy węzłów: `start`, `prompt`, `message`, `condition`, `http`, `variable`, `documentSearch`, `subAgent`, `databaseQuery`, `parallel`, `join`, `end`.

- Graf wykonuje się sekwencyjnie od Start, z wyjątkiem regionów `parallel`/`join` (fazy 2):
  ≥2 gałęzie z jednego `ParallelNode` biegną współbieżnie, każda z własną kopią zmiennych,
  scalanych w kolejności krawędzi przy wspólnym `JoinNode` (ostatnia gałąź wygrywa przy
  konflikcie klucza). Bez zagnieżdżonych regionów równoległych.
- Condition ma gałęzie `true`/`false`, porównania: `Equals`, `NotEquals`, `Contains`, `StartsWith`, `EndsWith`, `>`, `<`, `>=`, `<=`.
- Placeholdery w template'ach: `{input}`, `{variables.nazwa}`.
- Cykle (pętle) są dozwolone od fazy 2. Licznik pętli: `VariableNode.Value = "{variables.x+1}"`
  (lub `-N`) — jedyny obsługiwany prymityw arytmetyczny, tylko w tym jednym miejscu.
- Limit kroków wykonania: `MaxSteps` per wersja agenta (domyślnie 100, edytowalny w panelu agenta)
  — zabezpieczenie przed nieskończoną pętlą.

## Publikowanie i REST API

```
POST /api/agents/{id}/publish                     → immutable version
POST /api/agents/{id}/versions/{v}/conversations  → JSON reply
POST /api/agents/{id}/versions/{v}/stream         → SSE stream
```

Autoryzacja: nagłówek `X-Agent-Api-Key` (klucz pokazywany raz przy tworzeniu agenta).

Przykład:

```bash
curl -X POST http://localhost:5251/api/agents/$ID/versions/1/conversations \
  -H "Content-Type: application/json" \
  -H "X-Agent-Api-Key: ask_..." \
  -d '{"message":"hello","conversationId":null}'
```

`conversationId` utrzymuje wieloturową rozmowę trwale w Postgresie (faza 2) — bez TTL domyślnie,
przeżywa restart aplikacji. Opcjonalny job czyszczący stare rozmowy — `ConversationRetention`
w DEPLOYMENT.md, wyłączony domyślnie.

## Widget i publiczny chat

Każdy opublikowany agent ma stronę chatu do wysłania linkiem:

```
http://localhost:5251/chat/{agentId}/{version}?key=YOUR_API_KEY
```

Bez `?key=` strona pokazuje ekran wpisania klucza. Rozmowy są wieloturowe i trwałe (conversationId w Postgresie, bez TTL).

Widget do osadzenia na własnej stronie:

```html
<script src="http://localhost:5251/widget/agentstudio.js"></script>
<agent-studio-chat
    api-url="http://localhost:5251"
    agent-id="GUID"
    version="1"
    api-key="ask_...">
</agent-studio-chat>
```

## Narzędzie HTTP (bezpieczeństwo)

`HttpNode` obsługuje wszystkie metody, nagłówki, query params, JSON body, timeout i retry.
Wbudowana ochrona SSRF: blokada loopback, prywatnych zakresów IP, link-local; limit odpowiedzi 1 MB; max 3 przekierowania; filtrowanie nagłówków wrażliwych (`authorization`, `cookie`, `x-api-key`).

Aby dopuścić hosty wewnętrzne:

```json
"HttpTool": { "AllowedHosts": ["intranet.firma.local"] }
```

## Logi wykonania

Każde wywołanie zapisuje `ExecutionLog` z listą kroków (węzeł, typ, status, czas, błąd).
Sekrety i wrażliwe nagłówki nie są logowane.

## Testy

```bash
dotnet test
# 113 testów: walidator grafu (w tym parallel/join), evaluator warunków,
# runner (prompt/condition/http/multi-turn/loop/parallel/documentSearch/subAgent/databaseQuery),
# publish roundtrip, SSRF, REST API end-to-end (auth 401/404/400), user/role management, trwała
# pamięć rozmów, pętle (iteracje + MaxSteps guard), równoległość (fan-out/fan-in, merge,
# flush order), RAG (chunking, cosine ranking, indexer, search service), sub-agenci
# (depth-guard cyklu, wynik do zmiennej, błąd przy braku publikacji), konektory DB
# (SQL injection przeciwko prawdziwemu Postgresowi, read-only guard, obcięcie wyników),
# analityka (agregacja totals/errors/avg duration, filtr okna czasowego, wykonania w toku),
# formularze (merge formValues do zmiennych, publish/republish kopiuje MaxSteps+FormFields,
# FormFields getter toleruje niepoprawny/legacy JSON zamiast rzucać), retencja rozmów
# (purge starych konwersacji, świeże nietknięte), nieobsługiwany provider konektora DB
# odrzucany czytelnym błędem, szyfrowanie ApiKey (round-trip, GCM auth-tag chroni przed
# manipulacją, tolerancja dla wierszy sprzed włączenia szyfrowania)
```

## Wdrożenie (Windows/IIS)

Jedna aplikacja (`AgentStudio.Web`) jako jedna witryna IIS — nie ma osobnego `AgentStudio.Api`.
Pełna instrukcja: `DEPLOYMENT.md` (publish, PostgreSQL, IIS/web.config, reverse proxy dla SSE,
backup, smoke test po wdrożeniu).

## Logowanie (faza 2)

Panel studio (`/`, `/agents/*`, `/providers`, `/users`, `/database-connections`, `/analytics`) i zarządcze `/api/...` wymagają
zalogowania — konta z rolami Admin/Editor, cookie auth. Pierwsza wizyta na świeżej instalacji
przekierowuje `/` → `/login` → `/setup`, gdzie zakłada się pierwsze konto Admin. Kolejne konta
zarządzane są na `/users` (tylko Admin). Runtime endpointy agentów
(`/api/agents/{id}/versions/{v}/conversations`, `/stream`), `/chat/*` i `/widget/*` **zostają
publiczne** — chroni je wyłącznie `X-Agent-Api-Key`, bez zmian względem fazy 1.

`/auth/login` i `/auth/setup` mają rate limit: 5 żądań/minutę per IP klienta (429 powyżej limitu,
`AuthRateLimiting` w `Program.cs`, wbudowany rate limiter ASP.NET Core — zero nowych zależności).
To ochrona per-IP, nie lockout konta — za reverse proxy bez skonfigurowanego zaufania do
`X-Forwarded-For` limit dzieli się między wszystkich użytkowników za tym proxy (patrz
DEPLOYMENT.md).

`ModelProviderConfig.ApiKey` szyfrowany w bazie (AES-256-GCM, `SecretProtector` jako EF
`ValueConverter`) po ustawieniu klucza — `Secrets:EncryptionKey` w configu/env, opcjonalne (bez
klucza zapis pozostaje plaintext, jak dziś). Szczegóły i generowanie klucza — DEPLOYMENT.md.

## RAG i dokumenty (faza 2)

1. Ustaw `Embedding model` na providerze (`/providers`), np. `nomic-embed-text` dla Ollamy.
2. Na stronie agenta, zakładka "Documents" — wgraj plik tekstowy (`.txt`, `.md`, `.csv`,
   `.json`, `.log`; **binarne formaty jak PDF/DOCX nie są obsługiwane**).
3. Dodaj węzeł `documentSearch` do grafu — pole `Query` (domyślnie `{input}`), `Top K`,
   `Result variable`. Węzeł embeduje zapytanie i dopisuje najbardziej podobne fragmenty
   (brute-force cosine similarity, per agent) do zmiennej, gotowe do użycia w kolejnym
   `PromptNode` przez `{variables.nazwa}`.

Dokumenty żyją per agent — wyszukiwanie nie przeszukuje dokumentów innych agentów.

## Zakres fazy 1 (zrealizowany)

- ✅ Tworzenie agenta, prompt/system instructions, wybór modelu OpenAI-compatible
- ✅ Wizualny edytor grafu z węzłami: Start, LLM, Message, Condition, HTTP, Variable, End
- ✅ Testowy chat ze streamingiem
- ✅ Wieloturowa rozmowa przez `conversationId` (pamięć sesyjna)
- ✅ Wersjonowanie: Draft → Validated → Published (immutable)
- ✅ REST API + SSE + widget
- ✅ Statyczny API key per agent (hash SHA-256)
- ✅ Logi wykonania
- ✅ Ochrona SSRF w narzędziu HTTP

## Zakres fazy 2 (zrealizowany)

- ✅ Logowanie użytkowników z rolami Admin/Editor (cookie auth, bootstrap `/setup`)
- ✅ Trwała pamięć rozmów (Postgres, bez auto-wygasania — przeżywa restart aplikacji)
- ✅ Pętle w workflow (cykle dozwolone, `MaxSteps` per agent, licznik przez `{variables.x+1}`)
- ✅ Równoległość (fan-out/fan-in — węzły `parallel`/`join`, gałęzie równoległe, deterministyczny merge)
- ✅ RAG i dokumenty (`documentSearch`, brute-force cosine, upload plain text, zakładka Documents)

## Sub-agenci (faza 3)

Węzeł `subAgent` wywołuje inną **opublikowaną** wersję innego agenta jako jednorazowy,
bezstanowy krok — własna, jednorazowa rozmowa (nie dzieli historii z rodzicem), pełny tekst
wyniku trafia do zmiennej (`Result variable`), gotowy do użycia dalej w grafie przez
`{variables.nazwa}`. Zabezpieczenie przed cyklem agent-woła-agenta: sztywny limit głębokości
(5 zagnieżczeń). Wymaga wybrania agenta docelowego w `NodePropertiesEditor` — musi mieć
przynajmniej jedną opublikowaną wersję.

## Konektory do baz danych (faza 3)

1. Zdefiniuj połączenia w `appsettings.json` (sekcja `DatabaseConnections`, tablica) — **nie w
   panelu**, connection stringi to konfiguracja wdrożeniowa, nie dane aplikacji:
   ```json
   "DatabaseConnections": [
     { "Name": "reporting", "Provider": "postgres", "ConnectionString": "Host=...;...", "ReadOnly": true }
   ]
   ```
   `/database-connections` (Admin only) pokazuje listę tylko do odczytu (nazwa, provider,
   read-only) — bez connection stringów, bez dodawania/usuwania z panelu. Zmiana wymaga edycji
   configu i restartu aplikacji. `Provider` dziś obsługuje tylko `"postgres"` (Npgsql już jest
   zależnością) — pole istnieje pod przyszłe konektory (np. MySQL/SQL Server), żeby dodanie
   kolejnego nie wymagało zmiany kształtu configu; inna wartość odrzucana czytelnym błędem, nie
   cicho ignorowana.
2. Węzeł `databaseQuery` w grafie — wybierz connection, wpisz surowy SQL z placeholderami
   `@nazwa` (nigdy `{variables.x}` bezpośrednio w zapytaniu — to byłby SQL injection), a w
   polu `Parameters` (`nazwa=wartość` per linia) zmapuj każdy placeholder na
   `{input}`/`{variables.x}`. Wynik to JSON `{rows, rowCount, truncated}` w zmiennej.
3. Read-only connection przepuszcza tylko pojedyncze `SELECT` (heurystyka, nie twarda
   gwarancja — realna ochrona to uprawnienia użytkownika bazy). Limit 100 wierszy, powyżej —
   obcięcie z `truncated:true`, nie błąd.

## Formularze (faza 3)

Alternatywa dla czatu: zaprojektuj w zakładce "Form" na stronie agenta nazwane pola
(text/number/textarea/select, opcjonalnie wymagane), publikuj razem z grafem. Użytkownik
końcowy wypełnia formularz pod `/run/{agentId}/{version}?key=...` (ta sama bramka na klucz co
`/chat`) i dostaje jednorazowy wynik — bez wieloturowej rozmowy. Wartości pól stają się
`{variables.nazwaPola}` w grafie, tak samo jak każda inna zmienna.

## Analityka (faza 3)

`/analytics` (widoczna dla każdego zalogowanego) — stat tiles (liczba wykonań, error rate,
aktywni agenci, średni czas trwania) za ostatnie 30 dni, wykres słupkowy wykonań dziennie i
tabela z rankingiem agentów wg liczby wykonań. Agregacje LINQ nad istniejącym `ExecutionLog`
— żadnej nowej tabeli śledzącej, żadnej biblioteki wykresów (SVG ręcznie, jak `GraphEditor`).

## Zakres fazy 3 (zrealizowany)

- ✅ Sub-agenci (`subAgent`, jednorazowe wywołanie, depth-guard cyklu)
- ✅ Konektory do baz danych (`databaseQuery`, parametryzowane zapytania, read-only guard)
- ✅ Formularze do projektowania i uruchamiania agentów (`/run/{agentId}/{version}`, alternatywa dla czatu)
- ✅ Analityka biznesowa (`/analytics`, stat tiles + wykresy SVG)

Pełny plan: `PLAN.md` (sekcja "Plan dla AgentStudio — faza 3").

Poza zakresem (świadomie pominięte): Teams/Slack.
