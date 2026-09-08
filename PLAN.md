# Plan dla AgentStudio — faza 1

## 1. Cel fazy 1

Zbudować self-hosted studio do tworzenia, testowania i publikowania agentów AI:

- edytor wizualny workflow,
- konfiguracja promptów i modeli,
- wykonanie workflow,
- narzędzia HTTP,
- warunki if/else,
- testowy chat,
- obsługa wieloturowej rozmowy przez `conversationId`,
- streaming odpowiedzi,
- logi wykonania,
- publikowanie konkretnej, niezmiennej wersji,
- REST API,
- widget webowy.

Poza zakresem fazy 1:

- RAG i dokumenty,
- trwała pamięć rozmów,
- logowanie użytkowników,
- dowolny kod C#,
- pętle i równoległość,
- sub-agenci,
- konektory do baz danych,
- Teams/Slack,
- analityka biznesowa.

## 2. Założenia techniczne

- rozwiązanie: `AgentStudio`,
- .NET 10,
- Blazor Web App,
- Blazor.Diagrams,
- wdrożenie on-premise na Windows/IIS,
- PostgreSQL,
- lokalny filesystem,
- Microsoft Agent Framework dla .NET,
- Microsoft.Extensions.AI,
- adaptery OpenAI-compatible,
- obsługa Ollama, LM Studio, vLLM i podobnych endpointów,
- angielski interfejs użytkownika,
- panel administracyjny bez logowania,
- statyczny API key konfigurowany per agent,
- brak dowolnego wykonywania C# w fazie 1.

## 3. Architektura rozwiązania

Proponowana struktura projektów:

- `AgentStudio.Web`
  - Blazor Web App,
  - panel studio,
  - testowy chat,
  - konfiguracja agentów,
  - edytor Blazor.Diagrams.

- `AgentStudio.Api`
  - REST API dla opublikowanych agentów,
  - endpointy konwersacji,
  - streaming odpowiedzi,
  - walidacja API key.

- `AgentStudio.Application`
  - przypadki użycia,
  - zarządzanie agentami,
  - wersjonowanie,
  - publikowanie,
  - orkiestracja workflow.

- `AgentStudio.Domain`
  - encje i reguły domenowe,
  - definicja grafu,
  - typy węzłów,
  - wersje agenta,
  - konfiguracja narzędzi.

- `AgentStudio.Infrastructure`
  - PostgreSQL/EF Core,
  - dostawcy modeli,
  - cache konwersacji,
  - logowanie wykonania,
  - obsługa HTTP.

- `AgentStudio.Contracts`
  - DTO API,
  - modele komunikatów,
  - kontrakty streamingu.

## 4. Model agenta

Agent będzie posiadał:

- identyfikator,
- nazwę,
- opis,
- instrukcje systemowe,
- wybrany provider/model,
- konfigurację API key,
- wersję roboczą grafu,
- opublikowane wersje,
- statyczny API key,
- daty utworzenia i modyfikacji.

Konfiguracja API key nie powinna być zapisywana w bazie w postaci jawnej. Należy użyć szyfrowania wartości w konfiguracji lub bazie albo sekretu zewnętrznego.

## 5. Model workflow

Minimalny graf:

- `StartNode`,
- `MessageNode`,
- `PromptNode`,
- `ConditionNode`,
- `HttpNode`,
- `VariableNode`,
- `EndNode`.

Każdy workflow powinien mieć:

- dokładnie jeden węzeł Start,
- co najmniej jeden węzeł End,
- unikalne identyfikatory węzłów,
- połączenia skierowane,
- walidację braku niedostępnych węzłów,
- walidację niedozwolonych cykli,
- poprawnie zdefiniowane wyjścia warunkowe.

Wykonanie będzie sekwencyjne:

1. Start tworzy kontekst wykonania.
2. Węzeł zapisuje lub odczytuje wartości z kontekstu.
3. Następny węzeł jest wybierany na podstawie połączenia.
4. Condition wybiera gałąź `true` albo `false`.
5. End zwraca wynik do klienta.

## 6. Kontekst wykonania i conversationId

Kontekst będzie zawierał między innymi:

- `agentId`,
- `agentVersion`,
- `conversationId`,
- bieżące wejście użytkownika,
- zmienne workflow,
- wynik poprzednich węzłów,
- historię wiadomości bieżącej rozmowy,
- dane techniczne wykonania.

Rozmowy nie mają trwałej pamięci:

- `conversationId` będzie identyfikatorem danych przechowywanych tymczasowo,
- historia rozmowy może być przechowywana w pamięci procesu albo w cache,
- restart aplikacji usunie aktywne rozmowy,
- przy wielu instancjach aplikacji potrzebny będzie współdzielony cache, np. Redis.

Dla pierwszego wdrożenia na IIS proponuję:

- `IMemoryCache` jako prostą implementację,
- TTL konwersacji: 30–60 minut,
- limit liczby wiadomości i rozmiaru kontekstu,
- późniejsze przejście na Redis bez zmiany kontraktu aplikacyjnego.

Jest to pamięć sesyjna, a nie pamięć trwała.

## 7. Konfiguracja modeli

Warstwa modelu powinna korzystać z abstrakcji Microsoft.Extensions.AI.

Konfiguracja providera:

- nazwa,
- base URL,
- nazwa modelu,
- opcjonalny API key,
- timeout,
- parametry generowania,
- opcjonalne ustawienia streamingu.

Przykładowe endpointy:

- Ollama,
- LM Studio,
- vLLM,
- inne serwery zgodne z OpenAI API.

Należy oddzielić:

- konfigurację providera,
- konfigurację modelu,
- konfigurację konkretnego agenta.

Agent powinien wskazywać nazwę skonfigurowanego modelu, a nie przechowywać kompletną konfigurację połączenia.

## 8. Narzędzie HTTP

`HttpNode` powinien obsługiwać:

- GET,
- POST,
- PUT,
- PATCH,
- DELETE,
- HEAD,
- OPTIONS,
- własne nagłówki,
- query parameters,
- ścieżkę URL,
- JSON body,
- timeout,
- retry,
- mapowanie odpowiedzi do zmiennych.

Wymagane zabezpieczenia:

- blokada niebezpiecznych adresów lokalnych,
- ochrona przed SSRF,
- opcjonalna allowlista hostów,
- limit rozmiaru odpowiedzi,
- limit czasu wykonania,
- limit przekierowań,
- filtrowanie wrażliwych nagłówków,
- brak zapisywania sekretów w logach.

## 9. Warunki

Pierwsza wersja będzie obsługiwała proste porównanie:

- `==`,
- `!=`,
- `contains`,
- `startsWith`,
- `endsWith`,
- `>`,
- `<`,
- `>=`,
- `<=`.

Przykład:

```text
left:  variables.status
operator: ==
right: approved
```

Węzeł Condition będzie miał dwa wyjścia:

- `true`,
- `false`.

Nie będziemy uruchamiać dowolnych wyrażeń ani kodu użytkownika. Porównanie zostanie wykonane przez bezpieczny, własny evaluator.

## 10. Wersjonowanie i publikowanie

Cykl życia:

```text
Draft → Validated → Published
```

Zasady:

- draft jest edytowalny,
- walidacja odbywa się przed publikacją,
- opublikowana wersja jest immutable,
- API wskazuje konkretny numer wersji,
- zmiana agenta tworzy nowy draft lub nową wersję,
- nie można zmodyfikować opublikowanej wersji,
- można wycofać publikację,
- można opublikować starszą wersję ponownie.

Przykładowy adres:

```text
POST /api/agents/{agentId}/versions/{version}/conversations
```

W przyszłości można dodać alias:

```text
POST /api/agents/{agentId}/live/conversations
```

## 11. REST API i streaming

Podstawowe endpointy:

```text
GET    /api/agents
POST   /api/agents
GET    /api/agents/{id}
PUT    /api/agents/{id}/draft
POST   /api/agents/{id}/validate
POST   /api/agents/{id}/publish
GET    /api/agents/{id}/versions
POST   /api/agents/{id}/versions/{version}/conversations
POST   /api/agents/{id}/versions/{version}/stream
```

Uwierzytelnianie publikowanych agentów:

```http
X-Agent-Api-Key: ***
```

Klucz jest konfigurowany per agent. Dla fazy 1 proponuję Server-Sent Events (SSE) jako mechanizm streamingu.

## 12. Widget webowy

Widget powinien:

- przyjmować `agentId` i wersję albo alias,
- korzystać z API key,
- obsługiwać `conversationId`,
- wyświetlać odpowiedzi strumieniowo,
- pokazywać błędy,
- zachowywać `conversationId` w pamięci przeglądarki,
- mieć podstawową konfigurację wyglądu.

Widget można udostępnić jako:

```html
<script src="/widget/agentstudio.js"></script>
```

Konfiguracja:

```html
<agent-studio-chat
    agent-id="customer-support"
    api-url="https://server.example.com">
</agent-studio-chat>
```

Klucz API nie powinien być bezpośrednio osadzany w publicznym widgetcie bez dodatkowej kontroli. W pierwszej wersji widget powinien być przeznaczony do kontrolowanego środowiska lub otrzymywać krótkotrwały token generowany przez backend.

## 13. Logi wykonania

Każde wykonanie powinno mieć:

- `executionId`,
- `conversationId`,
- agent ID i wersję,
- czas rozpoczęcia i zakończenia,
- status,
- listę wykonanych węzłów,
- czasy poszczególnych węzłów,
- błędy,
- informacje o zużyciu modelu, jeśli provider je zwraca.

Nie należy logować:

- API keys,
- pełnych sekretów,
- wrażliwych nagłówków,
- całej treści rozmów bez jawnej decyzji,
- danych z HTTP body, jeśli mogą zawierać dane poufne.

## 14. Kolejność realizacji

> Status realizacji: patrz znaczniki ✅ przy każdym etapie. Etapy 1–10 zrealizowane (stan na 2026-09-08).
> Pełna weryfikacja kod-po-kodzie tego samego dnia: dodano zoom/pan w edytorze grafu (etap 3),
> usunięto martwy kod (`MaxRedirects` w `SecureHttpExecutor`, nieużywana zależność `Z.Blazor.Diagrams`),
> dopisano `ApiEndpointTests` pokrywające realne endpointy REST, auth i end-to-end SSE streaming
> (etap 10, `/versions/{v}/stream` przez Message-node graf bez fake'owania LLM). 41/41 testów zielone.

### Etap 1 — szkielet rozwiązania ✅

- utworzenie solucji `AgentStudio`,
- projekty warstwowe,
- konfiguracja PostgreSQL,
- podstawowy layout Blazor,
- health check,
- konfiguracja środowisk.

### Etap 2 — domena i zapis ✅

- encja agenta,
- draft,
- wersje,
- graf workflow,
- EF Core,
- migracje,
- walidacja domenowa.

### Etap 3 — edytor grafu ✅

- canvas ~~Blazor.Diagrams~~ własny edytor SVG (decyzja: mniejsza zależność, pełna kontrola nad portami/gałęziami condition),
- dodawanie i usuwanie węzłów,
- łączenie węzłów (drag z portu albo klik źródło→cel, gałęzie true/false dla condition),
- przesuwanie i zoom (kółko myszy wokół kursora + przyciski, panning przeciąganiem tła),
- panel właściwości,
- zapis grafu,
- walidacja grafu.

### Etap 4 — wykonawca workflow ✅

- kontekst wykonania,
- Start/End,
- Message,
- Variable,
- Condition,
- Prompt/LLM,
- obsługa błędów,
- limity wykonania.

### Etap 5 — integracja modeli ✅

- Microsoft.Extensions.AI,
- provider OpenAI-compatible,
- konfiguracja wielu endpointów,
- streaming odpowiedzi,
- obsługa Ollama i innych endpointów.

### Etap 6 — HTTP tool ✅

- wszystkie metody HTTP,
- parametry i body,
- mapowanie odpowiedzi,
- timeout/retry,
- ochrona SSRF,
- testy bezpieczeństwa (posprzątano martwy `MaxRedirects` w `SecureHttpExecutor` — limit przekierowań jest realnie wymuszany przez `HttpClientHandler.MaxAutomaticRedirections` w DI).

### Etap 7 — chat i API ✅

- testowy chat w studio,
- `conversationId`,
- pamięć tymczasowa,
- API key,
- REST API,
- SSE,
- logi wykonania.

### Etap 8 — wersjonowanie i publikacja ✅

- walidacja przed publikacją,
- immutable versions,
- endpointy wersji,
- wycofywanie publikacji,
- testy zgodności wersji.

### Etap 9 — widget ✅

- komponent chat,
- streaming,
- obsługa sesji,
- podstawowa konfiguracja,
- dokumentacja użycia.

### Etap 10 — testy i wdrożenie ✅

- testy jednostkowe domeny,
- testy wykonawcy,
- testy API (dopisano `ApiEndpointTests` — `WebApplicationFactory<Program>` uderza w realne endpointy REST z `Program.cs`: auth 401 brak/zły klucz, 404 nieopublikowana wersja, 400 brak providera, roundtrip draft→publish; wcześniej `ResolveRuntimeAsync` nie miał żadnego testu przez HTTP),
- testy widgetu (świadomie pominięte — czysty JS, brak frameworka testowego w projekcie, dodać jeśli widget urośnie ponad DOM+fetch),
- testy SSRF,
- testy streamingu (`Stream_endpoint_emits_sse_events_end_to_end` — realny `/stream` end-to-end),
- konfiguracja IIS,
- instrukcja wdrożenia,
- backup PostgreSQL.

## 15. Kryteria ukończenia fazy 1

Faza 1 będzie gotowa, gdy można:

1. Utworzyć agenta.
2. Skonfigurować model OpenAI-compatible.
3. Zbudować graf Start → Prompt → Condition → Message → End.
4. Dodać narzędzie HTTP z dowolną metodą HTTP.
5. Zapisać draft.
6. Zweryfikować graf.
7. Uruchomić testowy chat.
8. Otrzymywać odpowiedź strumieniowo.
9. Kontynuować rozmowę przy użyciu `conversationId`.
10. Opublikować immutable wersję.
11. Wywołać agenta przez REST API przy użyciu API key.
12. Uruchomić tego samego agenta przez widget.
13. Zobaczyć log wykonania.
14. Potwierdzić, że restart aplikacji usuwa tymczasowe rozmowy.
15. Potwierdzić ochronę HTTP toola przed SSRF.

## 16. Kluczowa decyzja

`conversationId` oznacza tymczasową sesję rozmowy, a nie trwałą pamięć. W pierwszej wersji sesje będą przechowywane w pamięci aplikacji i utracą się po restarcie.

> Odwrócone w fazie 2, etap 2 — patrz niżej: rozmowy stają się trwałe (Postgres), bez auto-wygasania.

---

# Plan dla AgentStudio — faza 2

## 1. Cel fazy 2

Zakres wybrany przez użytkownika z listy "poza zakresem fazy 1" (§1 wyżej):

1. RAG i dokumenty — nowy typ węzła do przeszukiwania dokumentów.
2. Trwała pamięć rozmów (odwraca §16 wyżej).
3. Pętle w workflow (cykle dziś blokowane przez `WorkflowValidator`).
4. Prawdziwa równoległość (fan-out/fan-in) w workflow.
5. Logowanie użytkowników z rolami (Admin/Editor) — dziś panel bez auth.

Pełny plan architektoniczny i uzasadnienia (ladder) — patrz `foamy-orbiting-fairy.md` w
`~/.claude/plans/` (zatwierdzony przez użytkownika przed startem implementacji, ten sam tryb
co przy planowaniu fazy 1).

### Dodatkowa poprawka: Postgres jako domyślna baza w dev, nie EF InMemory

`Agent`/`ModelProviderConfig` od zawsze idą przez EF Core (`AgentRepository`/
`ProviderRepository` → `AgentStudioDbContext`) — nigdy nie było ad-hoc listy w pamięci. Problem:
`appsettings.json` domyślnie ustawiał `ConnectionStrings:AgentStudio = "InMemory"`, więc
`dotnet run` bez dodatkowej konfiguracji używał EF InMemory provider — dane żyją tylko w
procesie, znikają przy restarcie `dotnet run`. To sprawiało wrażenie "zapisu w pamięci", mimo że
warstwa dostępu do danych była już poprawna.

Naprawa: `appsettings.Development.json` dostał domyślny connection string do prawdziwego
Postgresa (`Host=localhost;Port=5433;...` — port 5433, bo 5432 bywa zajęty przez inny lokalny
Postgres, jak na tej maszynie deweloperskiej). `docker-compose.yml` zaktualizowany do tego
samego portu. Bazowy `appsettings.json` zostaje z `"InMemory"` jako świadomy fallback dla
CI/szybkich testów bez Dockera.

Zweryfikowane end-to-end: podniesiony kontener Postgres (`agentstudio-postgres`, nazwany wolumen
`agentstudio_pgdata` — nie tymczasowy), `dotnet run` zaaplikował wszystkie 5 migracji, utworzony
agent + provider, **zabity proces aplikacji**, restart — `GET /api/agents/{id}` i
`GET /api/providers` zwróciły te same dane. Kontener zostaje uruchomiony (trwały, nie
posprzątany po teście, w przeciwieństwie do wcześniejszych jednorazowych testów w etapach 2/3).

**Złapana i naprawiona regresja w testach**: `ApiEndpointTests`' `WebApplicationFactory` też
czyta `appsettings.Development.json` (bo jawnie ustawia `UseEnvironment("Development")` z etapu
1) — po zmianie na realny Postgres, `Program.cs` rejestrował ORAZ Npgsql, ORAZ (przez
`RemoveAll`+re-`Add` w fabryce testowej) InMemory, co EF Core odrzuca ("only a single database
provider"). Naprawa: `ConfigureAppConfiguration` w fabryce testowej nadpisuje connection string
na `"InMemory"` *przed* tym jak `Program.cs` go odczyta, więc Npgsql nigdy się nie rejestruje.
79/79 testów zielonych po naprawie.

## 2. Kolejność realizacji

### Etap 1 — logowanie i role (Admin / Editor) ✅

- `User` entity + `AgentStudioDbContext.Users` + migracja.
- Cookie auth (`Microsoft.AspNetCore.Authentication.Cookies`), hasła przez wbudowany
  `PasswordHasher<T>` (zero nowych paczek — nie pełne ASP.NET Core Identity).
- `RequireAuthorization()` na zarządczym `/api` i stronach studio; runtime endpointy
  (`/versions/{v}/conversations`, `/stream`) i `/chat/*`, `/widget/*` zostają publiczne
  (chronione tylko `X-Agent-Api-Key` jak dotąd).
- Bootstrap pierwszego Admina (`/setup` gdy `Users` puste), strona zarządzania userami.
- Po drodze naprawiony realny bug: `UseStatusCodePagesWithReExecute` re-executował oryginalne
  żądanie (z JSON body) przeciw `/not-found` (strona Razor Components z antiforgery na POST),
  zamieniając czyste 401/404 z `/api` w mylące 400 "incorrect content-type". Naprawa:
  `UseWhen` wyłącza status-code-pages dla ścieżki `/api`, a `Events.OnRedirectToLogin` zwraca
  dla `/api` czysty 401 zamiast przekierowania do `/login`.
- Testy: `UserServiceTests` (hashowanie hasła, duplikat username, guard ostatniego admina przy
  delete/demote), `ApiEndpointTests` (401 bez logowania, pełny flow login jako bootstrap w
  `IAsyncLifetime`). 49/49 testów zielonych.
- Zweryfikowane end-to-end przez `curl` na żywym serwerze: `/` → `/login` → `/setup` → login →
  `/users` (200) → `/api/agents` (200) → logout → `/api/agents` (401); runtime `/conversations`
  i `/widget/agentstudio.js` potwierdzone jako nadal anonimowe.

### Etap 2 — trwała pamięć rozmów ✅

- `Conversations` w Postgresie (klucz `ConversationId`, `Messages`/`Variables` jako `jsonb`).
- `PersistentConversationStore : IConversationStore` zamiast `InMemoryConversationStore` (usunięty —
  martwy kod po podmianie) w DI — interfejs bez zmian, callerzy (`WorkflowRunner`,
  `StudioApiClient`, `PublicChat`, `Program.cs`) bez zmian. Rejestracja `AddScoped` (był
  `AddSingleton` — `PersistentConversationStore` zależy od `AgentStudioDbContext`, który jest
  Scoped).
- Brak auto-wygasania historii.
- Po drodze naprawiony realny bug leżący też w `ExecutionLog.Steps` od fazy 1: właściwości
  `jsonb`-konwertowane mutowane in-place (`state.Messages.Add(...)`, `variables[x]=y`) na
  śledzonej encji nie mają domyślnie `ValueComparer`, więc EF porównuje po referencji i **po
  cichu pomija UPDATE** przy `SaveChanges()` (widoczne wcześniej jako ostrzeżenie EF10620 w
  logach). Naprawa: `JsonValueComparer<T>` (serializacja do porównania + głęboki klon jako
  snapshot) dodany do `Messages`, `Variables` i retroaktywnie do `ExecutionLog.Steps`.
- Testy: `PersistentConversationStoreTests` (persystencja między instancjami `DbContext`,
  konflikt id między agentami tworzy nową rozmowę, brak TTL). Usunięte testy TTL
  `InMemoryConversationStore` (zachowanie świadomie odwrócone). 51/51 testów zielonych.
- Zweryfikowane end-to-end na **prawdziwym Postgresie** (tymczasowy kontener, nie EF InMemory):
  utworzono agenta, przeprowadzono rozmowę, **zabito proces aplikacji (`kill -9`, nie bazy)**,
  zrestartowano, kontynuowano tę samą `conversationId` — `psql` potwierdza jeden wiersz z 4
  wiadomościami (2 sprzed restartu + 2 po), `LastActivityAt` zaktualizowany. Test EF InMemory nie
  wystarczyłby tu — nie przetrwałby restartu procesu z definicji.

### Etap 3 — pętle w workflow ✅

- Zdjęta blokada cykli w `WorkflowValidator` (`HasCycle` usunięty, reachability-check zostaje
  bez zmian).
- `AgentVersion.MaxSteps` (default 100) zamiast stałej `const int maxSteps` w
  `WorkflowRunner.cs`; pole w UI ustawień agenta (`AgentDetail.razor`), przechodzi przez
  `UpdateDraftRequest`/`AgentVersionDto` (5 miejsc konstrukcji w `Program.cs` zaktualizowanych).
  Migracja `AddAgentVersionMaxSteps`.
- **Korekta w trakcie realizacji**: plan zakładał "żaden nowy typ węzła", ale okazało się że
  `VariableNode.Value` to czysta podstawa tekstowa (`ExpandTemplate`) — bez żadnej arytmetyki
  nie da się zbudować licznika pętli (`counter+1`), więc pętla ograniczona liczbą iteracji
  byłaby niewykonalna, tylko pętle napędzane zewnętrznie zmieniającym się stanem (HTTP/LLM) lub
  nieskończone (do `MaxSteps`). Dodano minimalny prymityw: `{variables.name+N}` /
  `{variables.name-N}` jako specjalny, całościowy wzorzec rozpoznawany tylko w
  `VariableNode.Value` (`WorkflowRunner.EvaluateVariableValue`) — nie ogólny evaluator wyrażeń,
  nie zmienia `ExpandTemplate` używanego przez Message/End/Http. Bez tego "pętle" byłyby
  funkcją technicznie obecną, ale praktycznie bezużyteczną.
- Testy: `Cycle_is_allowed_since_phase_2` (walidator), `Loop_runs_the_expected_number_of_iterations`
  (pełna pętla Variable→Condition→Message→Variable→cofnięcie, 3 iteracje, licznik),
  `Infinite_loop_is_stopped_by_MaxSteps` (self-loop, `MaxSteps=5`, błąd w streamie zamiast
  wyjątku niełapanego). 53/53 testów zielonych.
- Zweryfikowane end-to-end przez `curl` na żywym serwerze: cykliczny graf przechodzi walidację
  (`isValid:true`), `maxSteps` zapisany i odczytany poprawnie z API, konwersacja zwraca `"..."`
  (dokładnie 3 iteracje) — identyczny wynik jak w teście jednostkowym.

### Etap 4 — równoległość (fan-out/fan-in) ✅

- Nowe węzły `ParallelNode`/`JoinNode`.
- Walidacja: ≥2 krawędzie z `ParallelNode`, wszystkie gałęzie schodzą się w jednym wspólnym
  `JoinNode`, bez zagnieżdżonych regionów równoległych (explicit cut zakresu na fazę 2), bez
  ścieżek omijających split do tego samego joina.
- `WorkflowRunner`: przepisany na rekurencyjne `RunSegmentAsync` (top-level + per-gałąź).
  `Task.WhenAll` na gałęziach z lokalną kopią zmiennych, merge w kolejności krawędzi przy
  joinie, bufor streamu per gałąź (`ChunkSink` delegat) flush-owany dopiero po joinie (bez
  poprzeplatanych fragmentów w SSE), `steps` na `Interlocked`.
- `ExecutionLogWriter`: `lock (log)` przy `log.Steps.Add` (współbieżne gałęzie).
- **Złapany i naprawiony realny deadlock podczas implementacji** (nie hipotetyczny —
  `dotnet test` faktycznie zawiesił się na >120s): `WorkflowNodeConverter` (JSON polimorficzny
  deserializator) nie znał `"parallel"`/`"join"`, rzucał wyjątkiem przy `version.Graph`, który
  był **poza** `try/finally` w `ExecuteAsync` — `output.Complete()` nigdy się nie wywoływał,
  `RunAsync`'s `await foreach` po kanale wisiał wiecznie. To był latentny bug od zawsze (każdy
  przyszły wyjątek przed `try` miałby ten sam efekt). Naprawa: case'y w konwerterze + cały
  korpus `ExecuteAsync` (łącznie z `version.Graph`) przeniesiony do `try`, `output.Complete()`
  gwarantowane w `finally`.
- Testy: 5 walidatora (kształt, za mało gałęzi, różne joiny, gałąź kończąca się na End zamiast
  Join, zagnieżdżony split, bypass joina), 3 runnera (Http+Prompt branches wykonane
  współbieżnie, kolejność flush deterministyczna niezależnie od kolejności zakończenia,
  deterministyczny merge przy konflikcie klucza). 62/62 testów zielonych.
- Zweryfikowane end-to-end na żywym serwerze przez `curl`: graf równoległy przechodzi
  walidację, strona edytora renderuje nowe typy węzłów, wykonanie zwraca `"AB"` (jak w teście),
  log wykonania pokazuje wszystkie kroki bez korupcji mimo współbieżności.

### Etap 5 — RAG i dokumenty ✅

- `Document`/`DocumentChunk` (embedding jako `jsonb`, z `JsonValueComparer` z etapu 2 — bez
  tego mutacje in-place znów cicho gubiłyby zapisy), węzeł `DocumentSearchNode`
  (Query/TopK/ResultVariable).
- Embeddingi przez istniejący `Microsoft.Extensions.AI.OpenAI` (ten sam wzorzec co
  `OpenAiCompatibleChatClientFactory` — `client.GetEmbeddingClient(model).AsIEmbeddingGenerator()`)
  — zero nowych zależności NuGet. `ModelProviderConfig.EmbeddingModel` (opcjonalne pole).
- Wyszukiwanie: brute-force cosine similarity w procesie (`CosineSimilarity` w Domain), nie
  `pgvector` (`// ponytail:` w `DocumentSearchService` — pgvector nie ma odpowiednika w EF
  InMemory, złamałby wzorzec dev/prod; upgrade gdy korpus urośnie powyżej ~100k chunków).
- Chunking: `DocumentChunker` — proste okno znakowe z zakładką (1000/100 domyślnie), bez
  świadomości zdań/tokenów.
- Upload: **tylko czysty tekst** (`.txt`, `.md`, `.csv`, `.json`, `.log`) — świadomy cut zakresu,
  PDF/DOCX wymagałyby osobnej biblioteki do parsowania (większa decyzja zależnościowa,
  odłożona). Pliki na lokalnym filesystemie (`Documents:StoragePath`, domyślnie
  `App_Data/documents`), zakładka "Documents" w `AgentDetail.razor` (upload przez
  `InputFile`, lista, usuwanie).
- Testy: `DocumentChunkerTests` (długości/zakładka/rekonstrukcja), `CosineSimilarityTests`
  (identyczne/ortogonalne/przeciwne wektory, ranking), `DocumentSearchServiceTests`
  (deterministyczny fake embedding — ranking, pusty korpus, brak modelu → błąd),
  `DocumentIndexerTests` (brak modelu, zły typ pliku, pełny happy-path chunk+embed+store,
  delete usuwa wiersze i plik), `WorkflowRunnerTests.DocumentSearchNode_writes_joined_chunks...`
  (węzeł w grafie zapisuje wynik do zmiennej). 79/79 testów zielonych.
- Zweryfikowane end-to-end na żywym serwerze przez `curl`: graf z `documentSearch` przechodzi
  walidację i zapis, strona agenta renderuje panel "Documents (RAG)". Upload sam w sobie idzie
  przez Blazor `InputFile` (obwód SignalR) — nie do przetestowania curlem — więc rygor
  przeniesiony na testy jednostkowe `DocumentIndexerTests` zamiast smoke testu. Brak lokalnego
  serwera embeddingów (Ollama) w środowisku — rzeczywiste wywołanie embeddingu nie było
  testowane end-to-end na żywo, tylko przez fake w testach jednostkowych.

### Etap 6 — dokumentacja końcowa ✅

- README/DEPLOYMENT zaktualizowane pod auth, trwałą pamięć, RAG, pętle/równoległość (robione na
  bieżąco przy każdym etapie 1-5, nie jako oddzielny krok na końcu).
- Ostrzeżenie "no studio auth by design" w DEPLOYMENT.md usunięte i zastąpione instrukcją
  logowania + bootstrapu admina (etap 1).
- **Końcowy przegląd spójności złapał realne, nie tylko formalne, braki**: liczba testów w
  README (41 → 79, jedno miejsce nie zostało zaktualizowane przy wcześniejszych edycjach),
  mieszane porty w przykładach (5245 vs 5251 — ten sam proces, dwa różne porty w dokumentacji),
  sekcja "Wdrożenie" w README wciąż opisująca dwa osobne serwisy (`AgentStudio.Api` +
  `AgentStudio.Web`) — nigdy nieaktualizowany relikt sprzed decyzji "jedna aplikacja" z fazy 1
  (zastąpiony odnośnikiem do `DEPLOYMENT.md`, jedynego źródła prawdy), oraz `DEPLOYMENT.md`
  Option A (docker-compose) wciąż mówiąca "port 5432" mimo że `docker-compose.yml` teraz mapuje
  na 5433 (poprawka z tej sesji, patrz notatka wyżej o Postgresie w dev).

---

# Plan dla AgentStudio — faza 3

## 1. Cel fazy 3

Zakres wybrany przez użytkownika z listy "poza zakresem" (§1 wyżej), potwierdzony
AskUserQuestion: **sub-agenci, konektory do baz danych, analityka biznesowa**. Teams/Slack
świadomie pominięte. Dopisane w trakcie realizacji (prośba mid-turn): **formularze do
projektowania i uruchamiania agentów** (etap 4) — alternatywny, jednorazowy tryb uruchamiania
opublikowanego agenta obok czatu.

Pełny plan architektoniczny i uzasadnienia (ladder) — patrz `foamy-orbiting-fairy.md` w
`~/.claude/plans/` (zatwierdzony przez użytkownika przed startem implementacji, ten sam tryb
co przy planowaniu fazy 2).

## 2. Kolejność realizacji

Zrealizowane w kolejności 1 → 2 → 5 → 4 (analityka przed formularzami — formularze dopisane do
planu mid-turn już po starcie etapu 5), nie ściśle top-to-bottom — stąd licznik testów przy
każdym etapie niżej rośnie w kolejności wykonania, nie w kolejności czytania dokumentu (etap 4
kończy na 95/95, etap 5 wyżej w tekście zatrzymał się na 92/92 — to nie regresja, tylko
wcześniejszy stan).

### Etap 1 — sub-agenci ✅

- `SubAgentNode { TargetAgentId, InputTemplate, ResultVariable }` — wywołuje inną
  **opublikowaną** wersję innego agenta jako jednorazowe, bezstanowe wywołanie (własna
  `ConversationState`, nie dzieli historii z rodzicem, nie zapisywana przez
  `IConversationStore`). Wynik (pełny tekst, bez live-emit) trafia do zmiennej.
- Zabezpieczenie przed cyklem A→B→A: `callDepth` (limit 5, na sztywno), dodatkowy opcjonalny
  parametr `WorkflowRunner.RunAsync`/`RunSegmentAsync` — nie złamało istniejących wywołań
  (`Program.cs`, `StudioApiClient`, `PublicChat.razor` — domyślne `0`).
- `WorkflowRunner` zyskał `IAgentRepository`/`IProviderRepository` (już istniejące interfejsy,
  żadnych nowych). Istnienie agenta/wersji sprawdzane w runtime, nie w statycznym
  `WorkflowValidator`.
- Case `"subAgent"` dopisany do `WorkflowNodeConverter` (JSON) **od razu**, zanim cokolwiek
  innego — nauka z fazy 2 etap 4 zastosowana proaktywnie, nie trzeba było jej powtarzać.
- **Złapany i naprawiony realny bug podczas implementacji**: `WorkflowRunner.LastExecutionId`
  to pole instancji, a sub-agent rekurencyjnie woła `RunAsync` na `this` — zagnieżdżone
  wywołanie nadpisywało `LastExecutionId` rodzica cudzym id (i nie było to nawet bezpieczne
  wątkowo przy sub-agencie wewnątrz równoległej gałęzi z fazy 2). Naprawa: `LastExecutionId`
  ustawiane tylko gdy `callDepth == 0` — prostsze niż save/restore i eliminuje wyścig
  całkowicie, bo zagnieżdżone wywołania w ogóle go nie dotykają.
- UI: `subAgent` w palecie `GraphEditor`, wybór docelowego agenta w `NodePropertiesEditor`
  (nowy `[Parameter] AvailableAgents`, przekazywany z `AgentDetail.razor`).
- Testy: poprawny wynik do zmiennej (fake sub-agent graph), depth-guard zatrzymuje cykl A→A po
  5 wywołaniach (bez zawieszenia — sprawdzone z `timeout` w izolacji przed pełnym suite), brak
  opublikowanej wersji → czytelny błąd. 82/82 testów zielonych.
- Zweryfikowane end-to-end na żywym serwerze: agent A (sub-agent) wywołujący opublikowanego
  agenta B, `reply` = dokładny tekst z B, `executionId` w odpowiedzi to id egzekucji A (nie B —
  dowód że naprawa `callDepth==0` działa), log A pokazuje krok `subAgent`, log B ma własny,
  osobny, śledzalny wpis z `conversationId` prefiksem `subagent-`.
  (wymaga nowego `[Parameter]` z listą agentów, przekazanego z `AgentDetail.razor`).

### Etap 2 — konektory do baz danych ✅

- `DatabaseConnectionConfig { Name, ConnectionString, ReadOnly=true }` — tylko Postgres w v1,
  przez już zainstalowany `Npgsql` (zero nowych paczek NuGet — kompilowało się od razu, API
  zgadzało się z domysłem).
- `DatabaseQueryNode { ConnectionName, Query, Parameters, TimeoutSeconds, ResultVariable }` —
  **parametryzowane zapytania, nigdy string-concat** (SQL injection guard, analogiczny rygor
  do SSRF-guard przy `HttpNode`). Domyślnie read-only (heurystyka: zapytanie musi zaczynać się
  od `SELECT`, jedno stwierdzenie). Limit 100 wierszy z obcięciem (nie twardym failem —
  inaczej niż HTTP tool).
- Case `"databaseQuery"` dopisany do `WorkflowNodeConverter` od razu (nauka z fazy 2/3 etap 1
  zastosowana proaktywnie po raz drugi).
- Nowa strona `/database-connections` (Admin-only, wzorzec `/users` — bez REST API, tylko
  Blazor SignalR jak `/users`), węzeł `databaseQuery` w edytorze (connection picker + prosty
  edytor parametrów `name=value` per linia, bo `HttpNode`'s Headers/QueryParameters nie mają w
  ogóle edytora w UI, a tu parametryzacja jest rdzeniem bezpieczeństwa węzła, nie opcją).
- Testy: **6 testów przeciwko prawdziwemu Postgresowi** (localhost:5433, ten sam co trwała
  pamięć) — parametryzacja faktycznie chroni przed SQL injection (`'; DROP TABLE...` wraca
  jako dosłowna wartość, nie fragment SQL), odrzucenie DELETE i multi-statement na read-only
  connection, write-connection faktycznie przepuszcza nie-SELECT, obcięcie 150→100 wierszy
  (`generate_series`, bez potrzeby tabeli testowej), czytelny błąd na nieznanym connection.
  88/88 testów zielonych.
- Zweryfikowane end-to-end na żywym serwerze (nie tylko unit testy): węzeł `databaseQuery`
  odpytujący samą siebie (tabelę `DatabaseConnections`) przez sparametryzowane `@n`, wynik
  `{"rows":[{"Name":"self-pg"}],"rowCount":1,"truncated":false}` — dokładnie jak
  zaprojektowano. **Osobno zweryfikowany guard read-only na żywo**: `DELETE` przeciwko
  read-only connection zwrócił błąd w streamie, wiersz w bazie faktycznie przetrwał
  (potwierdzone bezpośrednim `psql` — nie tylko odczyt odpowiedzi API).

### Etap 4 — formularze do projektowania i uruchamiania agentów ✅

Dopisane w trakcie realizacji fazy 3 (prośba użytkownika mid-turn) — alternatywa dla czatu:
autor projektuje w studio nazwane pola formularza, użytkownik końcowy wypełnia formularz i
uruchamia opublikowaną wersję agenta, dostając wynik. Nie zastępuje czatu — dodatkowy tryb
uruchamiania obok `/chat/{agentId}/{version}`.

- `FormField { Name, Label, Type (text/number/textarea/select), Options, Required }` — lista
  na `AgentVersion` (`FormFieldsJson` jsonb, `FormFields` computed — dokładnie ten sam wzorzec
  serialize-on-set co `GraphJson`/`Graph`), więc jest częścią tego co się publikuje i jest
  niezmienne po publikacji, spójnie z resztą modelu wersjonowania. Migracja
  `AddAgentVersionFormFields` zaaplikowana na dev Postgresie.
- `WorkflowRunner.RunAsync`/`ExecuteAsync` zyskują opcjonalny `IReadOnlyDictionary<string,string>?
  formValues` (domyślnie null) — merge'owany do `conversation.Variables` PRZED
  `variables["input"] = userMessage`. Pola formularza stają się dostępne przez
  `{variables.nazwaPola}` w grafie, tak samo jak każda inna zmienna. Nie łamie istniejących
  wywołań (trailing opcjonalny parametr, jak `callDepth` w etapie 1).
- Studio UI: nowa sekcja "Form" na `AgentDetail.razor` (tabela wierszy pól, dodaj/usuń),
  edytowalna na draft, zapisywana przez `SaveAsync` razem z grafem i `MaxSteps`.
- Nowa strona runtime `/run/{agentId}/{version}` (wzorzec `PublicChat.razor` — ta sama bramka
  na klucz API, `?key=`), renderuje formularz z `FormFields` opublikowanej wersji (text/number/
  textarea/select, walidacja `Required` po stronie klienta przed submitem), po submit woła
  `WorkflowRunner.RunAsync` z `formValues` na jednorazowej, nietrwałej `ConversationState`
  (ten sam wzorzec co `SubAgentNode`'s throwaway conversation — `form-{guid}`, nigdy zapisywana
  przez `IConversationStore`) i pokazuje wynik — bez czata, jednorazowy request/response.
- **Złapany i naprawiony realny bug niezwiązany bezpośrednio z formularzami, ale w tej samej
  ścieżce kodu**: `AgentService.PublishAsync` i `RepublishAsync` tworzyły nową `AgentVersion`
  kopiując `Graph`/`GraphJson`, ale **nigdy nie kopiowały `MaxSteps`** z draftu/wersji
  źródłowej — opublikowana wersja zawsze dostawała domyślne 100, nawet jeśli autor świadomie
  obniżył limit (np. do 5, żeby złapać nieskończoną pętlę). Bug był niewidoczny w dotychczasowych
  testach (nikt nie sprawdzał `MaxSteps` po publikacji) i w praktyce niegroźny tylko dopóki
  nikt nie polegał na innej wartości niż domyślna. Naprawiono przy okazji dopisywania
  `FormFieldsJson` do tych samych dwóch konstruktorów (ten sam brakujący-copy wzorzec —
  naprawa jednym miejscem w każdej z dwóch metod, nie w wywołujących).
- Testy: `RunAsync` z `formValues` poprawnie ustawia zmienne przed startem grafu (fake graf
  czytający `{variables.pole}` w End template) — `WorkflowTests.FormValues_are_merged_into_variables_before_run`.
  Dwa nowe testy regresyjne na bug wyżej: `Publish_carries_MaxSteps_and_FormFields_from_draft`,
  `Republish_carries_MaxSteps_and_FormFields_from_source_version`. 95/95 testów zielonych.
- Zweryfikowane end-to-end na żywym serwerze z prawdziwym Postgresem: agent utworzony i
  opublikowany przez REST API (`POST /api/agents`, `PUT .../draft` z `formFields`, `POST
  .../publish`), `psql` bezpośrednio potwierdził `FormFieldsJson` identyczny na wersji draft
  (v0) i published (v1) — dowód że naprawa copy-bugu działa na żywej bazie, nie tylko w
  testach InMemory. `/run/{agentId}/1` bez klucza poprawnie pokazał bramkę
  ("This form requires an access key"), z `?key=` poprawnie wyrenderował oba pola ("City",
  "Age") z realnych opublikowanych `FormFields`. Sam submit formularza (SignalR/Blazor Server
  circuit) nie do zweryfikowania przez curl — logika po stronie silnika (merge `formValues` do
  zmiennych, expand w End template) ma pełne pokrycie unit-testem wyżej.
- **Drugi realny bug, złapany dopiero na żywym serwerze użytkownika (nie przez testy) po tym
  jak powyższe zostało już opisane jako "gotowe"**: migracja `AddAgentVersionFormFields` miała
  EF-owo wygenerowane `defaultValue: ""` dla nowej kolumny `jsonb` — dla istniejących wierszy
  (agentów sprzed tej migracji) Npgsql/Postgres skoercował to na `{}` (pusty obiekt JSON), nie
  `[]` (pusta tablica). `AgentVersion.FormFields` deserializował to jako `List<FormField>` i
  rzucał `JsonException` przy KAŻDYM otwarciu strony agenta z wersją sprzed migracji —
  `AgentDetail.razor` w ogóle nie renderował się dla żadnego istniejącego agenta (500,
  `DeveloperExceptionPageMiddleware`). Przyczyna: getter zakładał, że `FormFieldsJson` zawsze
  jest poprawną tablicą JSON — założenie prawdziwe dla świeżo tworzonych wersji (zawsze przez
  setter), fałszywe dla wierszy, które istniały przed dodaniem kolumny.
  Naprawa dwuwarstwowa: (1) getter `FormFields` teraz łapie `JsonException` i traktuje
  cokolwiek niebędące poprawną tablicą jako pustą listę zamiast wybuchać — chroni na przyszłość
  przed dowolnym innym nieoczekiwanym kształtem danych w tej kolumnie, nie tylko `{}`; (2)
  poprawiony `defaultValue` w pliku migracji na `"[]"` (dla świeżych instalacji od zera); (3)
  jednorazowy `UPDATE ... SET "FormFieldsJson" = '[]' WHERE "FormFieldsJson" = '{}'` na
  istniejącej dev bazie (13 wierszy). Test regresyjny:
  `FormFields_getter_tolerates_non_array_json_instead_of_throwing` (`""`, `"   "`, `"{}"`,
  `"not json"` — wszystkie muszą zwrócić pustą listę, nie rzucić). 99/99 testów zielonych.
  Zweryfikowane na żywo: ręcznie przywrócony `{}` na jednym wierszu dev bazy, `GET
  /agents/{id}` zwrócił 200 (wcześniej 500), wiersz przywrócony do `[]` po teście.

### Etap 5 — analityka biznesowa ✅

- `IAnalyticsRepository`/`AnalyticsRepository` — jedno zapytanie filtrujące `ExecutionLogs`
  (okno 30 dni) do pamięci, potem grupowanie LINQ-to-objects (per agent, per dzień) — żadnej
  nowej tabeli śledzącej, żadnego server-side `GroupBy` walczącego z tłumaczeniem EF
  (świadomie: przy skali dev/small-prod jedno zapytanie + agregacja w pamięci jest prostsze i
  równie szybkie co próba wymuszenia `DateOnly`-grupowania w SQL).
  `AnalyticsSummary { TotalExecutions, FailedExecutions, ActiveAgents, AvgDurationSeconds,
  ByAgent, ByDay }` — rekordy w `AgentStudio.Application/Interfaces.cs`, obok interfejsu (brak
  osobnego katalogu DTO na jeden feature).
- Nowa strona `/analytics` (widoczna dla każdego zalogowanego, nie Admin-only — w
  przeciwieństwie do `/database-connections`, bo to widok informacyjny, nie zarządzanie
  sekretami): stat tiles (executions, error rate, active agents, avg duration) + prosty
  wykres słupkowy SVG dzienny + poziome paski per-agent w tabeli. Bez nowej biblioteki JS —
  ręczne `<rect>` jak `GraphEditor.razor`.
- Testy: 4 nowe (`AnalyticsRepositoryTests`) — agregacja totals/errors/avg duration poprawna,
  wykonania spoza okna (30 dni) wykluczone, wykonania w trakcie (`CompletedAt == null`)
  wykluczone ze średniego czasu trwania ale liczą się do total, pusty zbiór zwraca wyzerowane
  podsumowanie zamiast rzucać. 92/92 testów zielonych.
- Zweryfikowane end-to-end na żywym serwerze z prawdziwym Postgresem: `/analytics` zwróciło
  200 i realne zagregowane dane z nagromadzonej historii wykonań z poprzednich sesji (SVG
  tooltip pokazał `2026-09-08: 9` — 9 wykonań dzisiaj, zgodne z faktycznym ruchem na
  instancji). Do logowania użyto tymczasowego konta testowego utworzonego przez
  `UserService` bezpośrednio na tej samej bazie (nie przez UI — `/auth/setup` już zwraca 404,
  bo administrator istnieje), usuniętego po teście.

### Etap 6 — dokumentacja końcowa ✅

- README/DEPLOYMENT/PLAN.md pod sub-agentów, konektory DB, formularze, analitykę.
- Pełny przegląd spójności na końcu (nie tylko dopisywanie na bieżąco) — powtórzyć to co
  złapało realne rozjazdy w fazie 2 etap 6.
- Znalezione i naprawione rozjazdy: licznik testów w README (88→95, w tym komentarz w bloku
  `dotnet test`), nagłówek "Zakres fazy 3 (w toku)" → "(zrealizowany)" (wszystkie 4 podpunkty
  są ✅, nagłówek nie nadążał), lista chronionych route'ów w sekcji "Logowanie" nie wymieniała
  `/database-connections` ani `/analytics` (dopisane w etapie 2/5, nigdy nie trafiły do tej
  listy), DEPLOYMENT.md nie wymieniał `/run/*` obok `/chat/*`/`/widget/*` w opisie topologii,
  security-note i regule reverse proxy (nowy publiczny endpoint z etapu 4, ten sam poziom
  ochrony co chat — pominięty przy pisaniu etapu 4, bo dopisywany na bieżąco, nie w ramach
  przeglądu). Dopisana notka wyjaśniająca, czemu liczniki testów przy etapach 4/5 w tym pliku
  nie rosną monotonicznie top-to-bottom (kolejność w dokumencie ≠ kolejność wykonania).
  Sprawdzone i bez rozjazdów: numery portów Postgresa (5433 dev, 5432 jako typowy default przy
  Option B w DEPLOYMENT.md — to rozróżnienie jest zamierzone, nie błąd), ścieżka migracji,
  `Documents:StoragePath` (zweryfikowane grepem względem `DocumentIndexer.cs`).
