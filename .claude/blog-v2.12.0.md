# Blog post — InferHub v2.12.0

**Status: DRAFTED, NOT POSTED.** The devart.solutions MCP connector dropped its session
(`Missing sessionId parameter`) mid-publish on 2026-07-21, so `create_post` never ran (the error
rejects the request before the insert — nothing landed, the slug is still free). Re-authorize the
connector, confirm the slug is absent via `list_posts`, then call `create_post` with the fields
below **once** (insert-only, unique slug, unrecoverable duplicates — see the connector notes).

- **slug:** `inferhub-2-12-stable-affinity`
- **isVisible_en:** `true`  ·  **isVisible_bg:** `false` (one shot — do not create hidden first)
- **author:** `Admin`
- **title_en:** InferHub 2.12: sticky routing that survives a reconnect — and a restart
- **title_bg:** InferHub 2.12: залепено маршрутизиране, което преживява преизключване — и рестарт
- **excerpt_en:** Sticky conversations now key on the stable node identity — so a node reconnecting keeps them, and with opt-in file persistence they survive a coordinator restart too. The groundwork for 3.0's warm failover, and no wrong answers if a hint goes stale.
- **excerpt_bg:** Залепените разговори вече се ключват към стабилната идентичност на възела — така че възел, който се преизключва, ги запазва, а с опционална файлова персистенция преживяват и рестарт на координатора. Основата за топлия failover в 3.0.

---

## content_en (HTML)

<p>Sticky routing is one of the quiet wins of running your own inference mesh. When several GPU nodes hold the same model, InferHub sends successive turns of a conversation back to the node that already served it — the one whose KV-cache is warm for that chat. The second turn skips the cold model load the first turn paid for. You never see it; you just notice the mesh feels fast.</p>

<p>In 2.12 we made that warmth durable. It used to evaporate in two places, and one of them was hiding in plain sight.</p>

<h2>The bug that wasn't a restart bug</h2>

<p>The obvious fragility was a coordinator restart: affinity lived only in memory, so bouncing the hub dropped every warm conversation. Fair enough — restarts are rare and you plan for them.</p>

<p>The non-obvious one was worse. Affinity was keyed to a node's <strong>SignalR connection id</strong>, and a connection id is <em>not stable across a node's own reconnect</em>. A node's network blips, its SignalR connection drops and re-establishes a second later with a brand-new id — the node never went anywhere, it was serving the whole time — and every conversation pinned to it was silently orphaned. The warm cache was still sitting right there on the GPU; the mesh had just forgotten how to find it. Nodes reconnect far more often than coordinators restart, so this was quietly costing cold reloads all day long.</p>

<h2>Re-key to the thing that doesn't change</h2>

<p>The fix is to key affinity on the <strong>stable node id</strong> — the identity a node keeps across every reconnect — instead of the ephemeral connection. The router resolves that node id to whatever connection the node currently holds, at dispatch time. A hint that points at a node which is disconnected, cordoned, or no longer holds the model simply isn't among the candidates, so it's a clean miss that falls through to the best fresh node. No stale pin ever routes to a ghost.</p>

<p>The consequence worth stating out loud: a mere disconnect <em>no longer forgets</em> affinity. A node reconnecting keeps its warm conversations, full stop. So does one that briefly missed its heartbeats and re-registered. Only an explicit admin de-register — an operator saying "this node is gone for good" — clears a node's affinity.</p>

<h2>Surviving a restart, if you ask for it</h2>

<p>On top of the re-key, 2.12 adds opt-in file persistence: set <code>Affinity:Persistence=file</code> and the map is written to disk as an append log with periodic compacted snapshots — the same raw-store discipline our local vector store already uses — and reloaded on startup, with any entry past its idle expiry dropped on the way in. Restart the coordinator mid-conversation and the next turn still lands on the same warm node.</p>

<p>It's off by default, and with it off the behaviour is byte-identical to 2.11. When it's on, the persisted map is deliberately <strong>a derived cache of routing hints, never a source of truth</strong>. Lose it, corrupt its last line in a crash, hand it a stale entry — the worst case is one cold model load, never a wrong answer. That's the line we hold: a performance cache is allowed to be lossy, an authority is not, and this one is firmly the former. A torn tail line on load is skipped, not treated as corruption.</p>

<p>And the privacy rule is untouched. The affinity key is still either a conversation header or a hash of the opening message — never content. What we persist is exactly three things: that key, a node id, and a timestamp. No prompts, no completions, not even a sample. There is no flag to change that, because a flag is an invitation.</p>

<h2>Why now</h2>

<p>This is the unglamorous prerequisite for something bigger. InferHub 3.0 will bring a warm-standby coordinator — a second hub that can take over when the primary fails. Warm failover only means anything if warm routing can survive the switch, and that requires affinity to key on a stable identity and be reloadable from shared state. That's exactly what landed here. We shipped it on its own so it could be verified on its own, rather than smuggled into the big release.</p>

<p>Verified, as always, on the published container and not just the test suite: pulled anonymously, run with persistence on against a fresh volume, and confirmed the coordinator writes its new <code>/data/affinity</code> directory as the non-root user it runs as — the exact class of permission trap that has bitten this project before and only ever shows up in the real image. Affinity entry count is now on <code>/api/status</code> and the <code>/metrics</code> scrape too.</p>

<p>Still self-hosted, still MIT, still zero new dependencies. <a href="https://github.com/Dev-Art-Solutions/InferHub">github.com/Dev-Art-Solutions/InferHub</a></p>

---

## content_bg (HTML)

<p>Залепеното маршрутизиране е една от тихите победи на това да въртиш собствен inference mesh. Когато няколко GPU възела държат един и същ модел, InferHub праща последователните реплики на един разговор обратно към възела, който вече го е обслужил — този, чийто KV-кеш е топъл за този чат. Втората реплика прескача студеното зареждане на модела, което първата е платила. Не го виждаш; просто усещаш, че mesh-ът е бърз.</p>

<p>В 2.12 направихме тази топлина трайна. Преди тя се изпаряваше на две места, и едното се криеше на видно място.</p>

<h2>Бъгът, който не беше бъг за рестарт</h2>

<p>Очевидната крехкост беше рестартът на координатора: афинитетът живееше само в паметта, така че рестарт на хъба изпускаше всеки топъл разговор. Приемливо — рестартите са редки и се планират.</p>

<p>Неочевидната беше по-лоша. Афинитетът се ключваше към <strong>SignalR connection id</strong>-то на възела, а connection id-то <em>не е стабилно през собственото преизключване на възела</em>. Мрежата на възела мигва, SignalR връзката пада и се възстановява секунда по-късно с чисто ново id — възелът никъде не е ходил, обслужвал е през цялото време — и всеки разговор, закачен за него, тихо осиротява. Топлият кеш още си стои на GPU-то; mesh-ът просто е забравил как да го намери. Възлите се преизключват много по-често, отколкото координаторите рестартират, така че това тихо струваше студени презареждания по цял ден.</p>

<h2>Ключване към това, което не се променя</h2>

<p>Поправката е афинитетът да се ключва към <strong>стабилното id на възела</strong> — идентичността, която възелът запазва през всяко преизключване — вместо към ефимерната връзка. Рутерът разрешава това id на възел до връзката, която възелът държи в момента, по време на диспечиране. Подсказка, сочеща към възел, който е изключен, cordon-нат или вече не държи модела, просто не е сред кандидатите — значи е чист пропуск, който пада към най-подходящия свеж възел. Никоя остаряла закачка не маршрутизира към призрак.</p>

<p>Следствието, което си заслужава да се каже на глас: обикновено изключване <em>вече не забравя</em> афинитет. Възел, който се преизключва, запазва топлите си разговори — точка. Същото важи и за възел, който за кратко е пропуснал heartbeat-ите си и се е пререгистрирал. Само изрично администраторско де-регистриране — оператор, който казва „този възел го няма завинаги" — изчиства афинитета на възел.</p>

<h2>Преживяване на рестарт, ако го поискаш</h2>

<p>Освен пре-ключването, 2.12 добавя опционална файлова персистенция: задай <code>Affinity:Persistence=file</code> и картата се записва на диск като append лог с периодични компактирани снапшоти — същата дисциплина на суровото хранилище, която локалното ни векторно хранилище вече използва — и се презарежда при стартиране, като всеки запис извън срока на бездействие се отхвърля на входа. Рестартирай координатора по средата на разговор и следващата реплика пак попада на същия топъл възел.</p>

<p>По подразбиране е изключено, и с изключено поведението е побитово идентично на 2.11. Когато е включено, персистираната карта е умишлено <strong>производен кеш от подсказки за маршрутизиране, никога източник на истина</strong>. Загуби я, повреди последния ѝ ред при срив, подай ѝ остарял запис — най-лошият случай е едно студено зареждане на модел, никога грешен отговор. Това е линията, която държим: на кеш за производителност му е позволено да губи, на авторитет — не, и този е категорично първото.</p>

<p>И правилото за поверителност е недокоснато. Ключът за афинитет е все така или заглавка на разговора, или хеш на началното съобщение — никога съдържание. Това, което персистираме, са точно три неща: този ключ, id на възел и времеви печат. Никакви промптове, никакви отговори, дори не извадка. Няма флаг, който да промени това, защото флагът е покана.</p>

<h2>Защо сега</h2>

<p>Това е непретенциозната предпоставка за нещо по-голямо. InferHub 3.0 ще донесе координатор в топъл резерв — втори хъб, който може да поеме, когато основният откаже. Топлият failover означава нещо само ако топлото маршрутизиране може да преживее превключването, а това изисква афинитетът да се ключва към стабилна идентичност и да е презареждаем от споделено състояние. Точно това дойде тук. Пуснахме го самостоятелно, за да може да се верифицира самостоятелно.</p>

<p>Верифицирано, както винаги, върху публикувания контейнер, а не само върху тестовете: изтеглено анонимно, пуснато с включена персистенция срещу свеж том, и потвърдено, че координаторът записва новата си директория <code>/data/affinity</code> като non-root потребителя, под който върви — точно класът капан с правата, който вече е хапал този проект и се появява само в реалния образ.</p>

<p>Все така self-hosted, все така MIT, все така нула нови зависимости. <a href="https://github.com/Dev-Art-Solutions/InferHub">github.com/Dev-Art-Solutions/InferHub</a></p>
