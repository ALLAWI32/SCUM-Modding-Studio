namespace ScumStudio.Modding.ServerMods;

/// <summary>How far a Lua function of the catalog was tried on the real server.</summary>
public enum LuaApiStatus
{
    /// <summary>Called on the owner's running server and did what the description says.</summary>
    Tested,

    /// <summary>Exists in the server's class dump (or was hooked) but its effect was not tried to the end.</summary>
    Seen,

    /// <summary>Does not work or crashes the server: kept so nobody tries it again.</summary>
    Unsafe,
}

/// <summary>What a catalog entry is.</summary>
public enum LuaApiKind
{
    /// <summary>A function the script calls.</summary>
    Function,

    /// <summary>An event of the game the script reacts to (<c>RegisterHook</c>).</summary>
    Hook,

    /// <summary>A ready piece of script that does a job.</summary>
    Recipe,

    /// <summary>A rule learned the hard way.</summary>
    Rule,
}

/// <summary>One entry of the Lua catalog: what it is, how to write it and what happened when it was tried.</summary>
/// <param name="Id">Stable key.</param>
/// <param name="Group">Group name (one of <see cref="LuaApiCatalog.Groups"/>).</param>
/// <param name="Kind">Function, hook, recipe or rule.</param>
/// <param name="Name">What the script writes, like <c>controller:SendHUDMessageToClient</c>.</param>
/// <param name="Signature">The call with its arguments.</param>
/// <param name="Status">How far it was tried.</param>
/// <param name="Text">English description.</param>
/// <param name="TextRu">Russian description.</param>
/// <param name="Snippet">Lua to insert into the editor.</param>
/// <param name="GamePath">The game function it is about (<c>/Script/SCUM.Class:Function</c>), checked against the class dump; empty for UE4SS's own functions.</param>
public sealed record LuaApiEntry(
    string Id,
    string Group,
    LuaApiKind Kind,
    string Name,
    string Signature,
    LuaApiStatus Status,
    string Text,
    string TextRu,
    string Snippet,
    string GamePath = "");

/// <summary>
/// The functions, events and rules a server Lua mod can use, with what was really seen on the owner's server (SCUM 1.3.4.2, UE4SS):
/// every <see cref="LuaApiStatus.Tested"/> entry was run there. The class dump (<c>ue4ss\scum_dump.txt</c>) tells which of the
/// others exist at all.
/// </summary>
public static class LuaApiCatalog
{
    private const string Rpc = "/Script/SCUM.PlayerRpcChannel:";

    /// <summary>The groups in display order.</summary>
    public static IReadOnlyList<string> Groups { get; } = ["Basics", "Players", "Messages", "World", "Chat", "Hooks", "Recipes", "Safety"];

    /// <summary>All entries.</summary>
    public static IReadOnlyList<LuaApiEntry> All { get; } =
    [
        // ---- Basics: UE4SS's own functions ----
        new("ExecuteWithDelay", "Basics", LuaApiKind.Function, "ExecuteWithDelay", "ExecuteWithDelay(milliseconds, function() ... end)", LuaApiStatus.Tested,
            "Runs the function once after a pause. It runs on a helper thread: touch the game from it only through ExecuteInGameThread.",
            "Выполняет функцию один раз через паузу. Работает в отдельном потоке: к игре обращайтесь только через ExecuteInGameThread.",
            "ExecuteWithDelay(1000, function()\n  ExecuteInGameThread(function()\n    -- the game may be touched here\n  end)\nend)"),
        new("ExecuteInGameThread", "Basics", LuaApiKind.Function, "ExecuteInGameThread", "ExecuteInGameThread(function() ... end)", LuaApiStatus.Tested,
            "Runs the function on the game's own thread, the only place where players, actors and classes may be read or changed.",
            "Выполняет функцию в основном потоке игры. Только там можно читать и менять игроков, акторов и классы.",
            "ExecuteInGameThread(function()\n  -- the game may be touched here\nend)"),
        new("LoopAsync", "Basics", LuaApiKind.Function, "LoopAsync", "LoopAsync(milliseconds, function() ... return false end)", LuaApiStatus.Tested,
            "Repeats the function every pause until it returns true. Return false to go on. Put the game work inside ExecuteInGameThread and keep it light: a slow loop makes the server watchdog kill the game.",
            "Повторяет функцию через паузу, пока она не вернёт true. Вернув false, цикл продолжается. Работу с игрой кладите в ExecuteInGameThread и делайте лёгкой: тяжёлый цикл убивает сервер по таймауту.",
            "ExecuteWithDelay(30000, function()\n  LoopAsync(10000, function()\n    ExecuteInGameThread(function()\n      -- every 10 seconds\n    end)\n    return false\n  end)\nend)"),
        new("RegisterHook", "Basics", LuaApiKind.Function, "RegisterHook", "RegisterHook(\"/Script/SCUM.Class:Function\", function(self, ...) end)", LuaApiStatus.Tested,
            "Calls your function every time the game runs the named function. The arguments arrive wrapped: read them with :get(). Never touch a player inside the hook: remember who it was and answer 0.5-3 s later with ExecuteWithDelay + ExecuteInGameThread.",
            "Вызывает вашу функцию каждый раз, когда игра выполняет названную функцию. Аргументы приходят в обёртке, читайте через :get(). Не трогайте игрока внутри хука: запомните его и ответьте через 0.5-3 с через ExecuteWithDelay + ExecuteInGameThread.",
            "RegisterHook(\"" + Rpc + "Prisoner_Server_Die\", function(self)\n  -- remember, do not touch the player here\nend)"),
        new("FindFirstOf", "Basics", LuaApiKind.Function, "FindFirstOf", "FindFirstOf(\"ClassName\")", LuaApiStatus.Tested,
            "The first live object of a class (Blueprint classes end in _C, like BP_ConZGameMode_C). A miss still returns a table: always check :IsValid() before using it.",
            "Первый живой объект класса (у Blueprint-классов окончание _C, например BP_ConZGameMode_C). При промахе возвращает пустышку: всегда проверяйте :IsValid().",
            "local gm = FindFirstOf(\"BP_ConZGameMode_C\")\nif gm and gm:IsValid() then\n  -- use gm\nend"),
        new("FindAllOf", "Basics", LuaApiKind.Function, "FindAllOf", "FindAllOf(\"ClassName\")", LuaApiStatus.Tested,
            "All live objects of one class as a list, or nil. Light for BP_Prisoner_C (players). Never loop it over many classes: that froze the game for 20 s and the watchdog killed the server.",
            "Все живые объекты одного класса списком или nil. Для BP_Prisoner_C (игроки) лёгкая. Не гоняйте её циклом по многим классам: игра зависла на 20 с, и сервер убил сторож.",
            "local ok, list = pcall(FindAllOf, \"BP_Prisoner_C\")\nif ok and type(list) == \"table\" then\n  for _, p in ipairs(list) do\n    if p:IsValid() then\n      -- one player\n    end\n  end\nend"),
        new("StaticFindObject", "Basics", LuaApiKind.Function, "StaticFindObject", "StaticFindObject(\"/Script/SCUM.Default__WorldStatics\")", LuaApiStatus.Tested,
            "Finds an object or a loaded class by its full path. The helper classes with static functions are found as Default__<Class>.",
            "Находит объект или загруженный класс по полному пути. Классы со статическими функциями ищутся как Default__<Класс>.",
            "local statics = StaticFindObject(\"/Script/SCUM.Default__WorldStatics\")"),
        new("LoadAsset", "Basics", LuaApiKind.Function, "LoadAsset", "LoadAsset(\"/Game/.../BP_Name.BP_Name\")", LuaApiStatus.Tested,
            "Loads a Blueprint that is not in memory yet. Use the FULL object path (folder, dot, name); the class then appears under the same path with _C.",
            "Подгружает Blueprint, которого ещё нет в памяти. Нужен ПОЛНЫЙ путь объекта (папка, точка, имя); класс потом лежит там же с окончанием _C.",
            "local PATH = \"/Game/ConZ_Files/Characters/NPCs/Armed_NPCs/Blueprint/Guard/BP_Guard_Lvl_3.BP_Guard_Lvl_3\"\npcall(function() LoadAsset(PATH) end)\nlocal cls = StaticFindObject(PATH .. \"_C\")"),
        new("pcall", "Basics", LuaApiKind.Function, "pcall", "local ok, result = pcall(function() ... end)", LuaApiStatus.Tested,
            "Wraps a risky game call so that an error is returned instead of stopping the whole mod. Wrap every call that touches a player or an actor.",
            "Оборачивает рискованный вызов игры: вместо остановки всего мода вернётся ошибка. Оборачивайте каждый вызов, трогающий игрока или актора.",
            "local ok, result = pcall(function() return player:K2_GetActorLocation() end)"),
        new("print", "Basics", LuaApiKind.Function, "print", "print(\"text\\n\")", LuaApiStatus.Tested,
            "Writes a line to UE4SS.log. End the text with \\n, or the next line is glued to it.",
            "Пишет строку в UE4SS.log. Заканчивайте текст на \\n, иначе следующая строка прилипнет.",
            "print(\"[MyMod] started\\n\")"),

        // ---- Players ----
        new("Players.List", "Players", LuaApiKind.Recipe, "All players (BP_Prisoner_C)", "FindAllOf(\"BP_Prisoner_C\")", LuaApiStatus.Tested,
            "BP_Prisoner_C is a player's character; the list holds only the players that are online. K2_GetActorLocation gives X, Y, Z in centimetres.",
            "BP_Prisoner_C - персонаж игрока; в списке только те, кто в сети. K2_GetActorLocation даёт X, Y, Z в сантиметрах.",
            "local list = FindAllOf(\"BP_Prisoner_C\") or {}\nfor _, p in ipairs(list) do\n  if p:IsValid() then\n    local at = p:K2_GetActorLocation()\n    print(string.format(\"player at %.0f %.0f %.0f\\n\", at.X, at.Y, at.Z))\n  end\nend"),
        new("Controllers", "Players", LuaApiKind.Function, "BP_ConZPlayerController_C", "FindAllOf(\"BP_ConZPlayerController_C\")", LuaApiStatus.Tested,
            "A player's controller: it holds the name (PlayerState.PlayerNamePrivate) and receives private messages. One per connected player.",
            "Контроллер игрока: хранит имя (PlayerState.PlayerNamePrivate) и принимает личные сообщения. По одному на подключённого игрока.",
            "local pcs = FindAllOf(\"BP_ConZPlayerController_C\") or {}"),
        new("PlayerName", "Players", LuaApiKind.Recipe, "Player's name", "pc.PlayerState.PlayerNamePrivate:ToString()", LuaApiStatus.Tested,
            "The name of the player of a controller. Wrap it in pcall: right after a join the state may not be ready.",
            "Имя игрока контроллера. Оберните в pcall: сразу после входа состояние может быть не готово.",
            "local function nameOf(pc)\n  local ok, name = pcall(function() return pc.PlayerState.PlayerNamePrivate:ToString() end)\n  return ok and name or \"?\"\nend"),
        new("GetController", "Players", LuaApiKind.Function, "actor:GetController", "actor:GetController()", LuaApiStatus.Tested,
            "The controller of a character. A dead guard or player has none any more, which is how a script sees that somebody died.",
            "Контроллер персонажа. У погибшего охранника или игрока его уже нет, по этому скрипт и узнаёт о смерти.",
            "local ok, c = pcall(function() return actor:GetController() end)\nlocal alive = ok and c ~= nil and c:IsValid()"),
        new("K2_GetActorLocation", "Players", LuaApiKind.Function, "actor:K2_GetActorLocation", "actor:K2_GetActorLocation() -> {X, Y, Z}", LuaApiStatus.Tested,
            "Where an actor stands, in centimetres (X forward, Y right, Z up).",
            "Где стоит актор, в сантиметрах (X вперёд, Y вправо, Z вверх).",
            "local at = actor:K2_GetActorLocation()\nprint(at.X, at.Y, at.Z)"),
        new("K2_DestroyActor", "Players", LuaApiKind.Function, "actor:K2_DestroyActor", "actor:K2_DestroyActor()", LuaApiStatus.Tested,
            "Removes an actor from the world (a guard you spawned, for instance). Not for players.",
            "Удаляет актор из мира (например, созданного вами охранника). Не для игроков.",
            "if actor and actor:IsValid() then pcall(function() actor:K2_DestroyActor() end) end"),

        // ---- Messages ----
        new("SendHUDMessageToClient", "Messages", LuaApiKind.Function, "controller:SendHUDMessageToClient", "controller:SendHUDMessageToClient(text, nil)", LuaApiStatus.Tested,
            "Shows one line on ONE player's screen, nobody else sees it. The second argument is a sound (nil for none). Only one line fits: line breaks are not shown.",
            "Показывает одну строку на экране ОДНОГО игрока, остальные не видят. Второй аргумент - звук (nil - без звука). Помещается одна строка: переносы не выводятся.",
            "pcall(function() controller:SendHUDMessageToClient(\"Hello!\", nil) end)",
            "/Script/SCUM.ConZPlayerController:SendHUDMessageToClient"),
        new("SendHUDMessageToAll", "Messages", LuaApiKind.Function, "gamemode:SendHUDMessageToAll", "gm:SendHUDMessageToAll(text, false)", LuaApiStatus.Tested,
            "Shows a line on every player's screen. The second argument says whether to play a beep.",
            "Показывает строку на экране каждого игрока. Второй аргумент - играть ли сигнал.",
            "local gm = FindFirstOf(\"BP_ConZGameMode_C\")\nif gm and gm:IsValid() then pcall(function() gm:SendHUDMessageToAll(\"Hello all\", false) end) end",
            "/Script/SCUM.ConZGameMode:SendHUDMessageToAll"),

        // ---- World ----
        new("GetTimeOfDay", "World", LuaApiKind.Function, "WorldStatics:GetTimeOfDay", "statics:GetTimeOfDay(gamemode) -> hours", LuaApiStatus.Tested,
            "The game's clock as hours with a fraction (13.5 is 13:30).",
            "Игровое время часами с дробью (13.5 - это 13:30).",
            "local gm = FindFirstOf(\"BP_ConZGameMode_C\")\nlocal statics = StaticFindObject(\"/Script/SCUM.Default__WorldStatics\")\nlocal hours = statics:GetTimeOfDay(gm)\nlocal h = math.floor(hours)\nprint(string.format(\"%02d:%02d\\n\", h, math.floor((hours - h) * 60)))",
            "/Script/SCUM.WorldStatics:GetTimeOfDay"),
        new("SpawnActor", "World", LuaApiKind.Function, "world:SpawnActor", "world:SpawnActor(class, {X=,Y=,Z=}, {Pitch=,Yaw=,Roll=})", LuaApiStatus.Tested,
            "Puts a new actor into the world. Worked for armed guards (BP_Guard_Lvl_3/4/5): they get their own AI and shoot. Load the class first with LoadAsset. It does NOT give items to players.",
            "Создаёт актор в мире. Сработало для вооружённых охранников (BP_Guard_Lvl_3/4/5): у них свой ИИ, они стреляют. Класс сначала подгрузите через LoadAsset. Предметы игрокам НЕ выдаёт.",
            "local world = player:GetWorld()\nlocal ok, guard = pcall(function()\n  return world:SpawnActor(cls, { X = 0, Y = 0, Z = 0 }, { Pitch = 0, Yaw = 0, Roll = 0 })\nend)"),
        new("GetWorld", "World", LuaApiKind.Function, "actor:GetWorld", "actor:GetWorld()", LuaApiStatus.Tested,
            "The world an actor lives in; SpawnActor is called on it.",
            "Мир, в котором живёт актор; у него вызывается SpawnActor.",
            "local world = player:GetWorld()"),

        // ---- Chat ----
        new("Chat.Command", "Chat", LuaApiKind.Hook, "Chat_Server_BroadcastChatMessage", "RegisterHook(\"" + Rpc + "Chat_Server_BroadcastChatMessage\", function(self, Message, Channel) end)", LuaApiStatus.Tested,
            "Fires for every normal chat line. Message:get():ToString() is the text; self:get():GetOuter() (after a pause) is the player's controller. Everybody still sees what the player wrote: a chat line cannot be hidden. Do not start commands with # (the game takes those as admin commands); use ! .",
            "Срабатывает на каждую обычную строку чата. Message:get():ToString() - текст; self:get():GetOuter() (после паузы) - контроллер игрока. Остальные всё равно видят написанное: строку чата скрыть нельзя. Не начинайте команды с # (игра считает их админскими), берите !.",
            "RegisterHook(\"" + Rpc + "Chat_Server_BroadcastChatMessage\", function(self, Message, Channel)\n  local ok, text = pcall(function() return Message:get():ToString() end)\n  if not ok or string.lower(text or \"\") ~= \"!hello\" then return end\n  local channel\n  pcall(function() channel = self:get() end)\n  ExecuteWithDelay(800, function()\n    ExecuteInGameThread(function()\n      if not (channel and channel:IsValid()) then return end\n      local player = channel:GetOuter()\n      if player and player:IsValid() then pcall(function() player:SendHUDMessageToClient(\"Hello!\", nil) end) end\n    end)\n  end)\nend)",
            Rpc + "Chat_Server_BroadcastChatMessage"),
        new("Chat.Admin", "Chat", LuaApiKind.Hook, "Chat_Server_ProcessAdminCommand", "RegisterHook(\"" + Rpc + "Chat_Server_ProcessAdminCommand\", function(self, Command) end)", LuaApiStatus.Seen,
            "Fires for a chat line that starts with # (the admin commands, text without the #). It also runs a command: channel:Chat_Server_ProcessAdminCommand(\"SpawnItem ...\") works only for players listed as admins.",
            "Срабатывает на строку чата с # (админские команды, текст без #). Им же можно выполнить команду: channel:Chat_Server_ProcessAdminCommand(\"SpawnItem ...\") работает только для админов из списка.",
            "RegisterHook(\"" + Rpc + "Chat_Server_ProcessAdminCommand\", function(self, Command)\n  local ok, text = pcall(function() return Command:get():ToString() end)\nend)",
            Rpc + "Chat_Server_ProcessAdminCommand"),

        // ---- Hooks (events) ----
        new("Hook.Login", "Hooks", LuaApiKind.Hook, "SurvivalStats_Server_HandlePlayerLogin", "RegisterHook(\"" + Rpc + "SurvivalStats_Server_HandlePlayerLogin\", function() end)", LuaApiStatus.Tested,
            "A player joined. Reading the player inside the hook crashed the server: only ask for a look at the player list 3 s later.",
            "Игрок зашёл. Чтение игрока внутри хука роняло сервер: только запланируйте просмотр списка игроков через 3 с.",
            "RegisterHook(\"" + Rpc + "SurvivalStats_Server_HandlePlayerLogin\", function()\n  ExecuteWithDelay(3000, function() ExecuteInGameThread(function()\n    -- look at the player list here\n  end) end)\nend)",
            Rpc + "SurvivalStats_Server_HandlePlayerLogin"),
        new("Hook.Logout", "Hooks", LuaApiKind.Hook, "SurvivalStats_Server_HandlePlayerLogout", "RegisterHook(\"" + Rpc + "SurvivalStats_Server_HandlePlayerLogout\", function() end)", LuaApiStatus.Tested,
            "A player left. Same care as the login hook.",
            "Игрок вышел. Те же меры предосторожности, что у входа.",
            "RegisterHook(\"" + Rpc + "SurvivalStats_Server_HandlePlayerLogout\", function()\n  ExecuteWithDelay(3000, function() ExecuteInGameThread(function()\n    -- look at the player list here\n  end) end)\nend)",
            Rpc + "SurvivalStats_Server_HandlePlayerLogout"),
        new("Hook.Die", "Hooks", LuaApiKind.Hook, "Prisoner_Server_Die", "RegisterHook(\"" + Rpc + "Prisoner_Server_Die\", function() end)", LuaApiStatus.Tested,
            "A player died. Log it without touching the player (the name is not safe to read here).",
            "Игрок умер. Записывайте событие, не трогая игрока (читать имя здесь небезопасно).",
            "RegisterHook(\"" + Rpc + "Prisoner_Server_Die\", function()\n  print(\"a player died\\n\")\nend)",
            Rpc + "Prisoner_Server_Die"),
        new("Hook.Examine", "Hooks", LuaApiKind.Hook, "ExamineObjectOnServer", "RegisterHook(\"/Script/SCUM.InteractionComponent:ExamineObjectOnServer\", function(self, Component, MeshInstanceId, Vector) end)", LuaApiStatus.Tested,
            "A player searched (\"examined\") a world object. Component:get():GetFullName() names the part (level, actor, component); self:get():GetOuter() is the player. It works as a private button: put an object in the world and react to its name.",
            "Игрок обыскал объект мира. Component:get():GetFullName() называет часть (уровень, актор, компонент); self:get():GetOuter() - игрок. Годится как личная кнопка: поставьте объект и реагируйте на его имя.",
            "RegisterHook(\"/Script/SCUM.InteractionComponent:ExamineObjectOnServer\", function(self, Component)\n  local ok, name = pcall(function() return Component:get():GetFullName() end)\n  if ok and name and name:find(\"MyObject\", 1, true) then\n    -- remember the player, answer later\n  end\nend)",
            "/Script/SCUM.InteractionComponent:ExamineObjectOnServer"),
        new("Hook.Quest", "Hooks", LuaApiKind.Hook, "Server_StartQuest", "RegisterHook(\"/Script/SCUM.PlayerQuestComponent:Server_StartQuest\", function(self) end)", LuaApiStatus.Seen,
            "A player started a quest at a quest giver. Only reading works: the personal trade deals are native state and cannot be added from Lua.",
            "Игрок взял задание у заказчика. Работает только чтение: личные торговые сделки - внутреннее состояние игры, из Lua их не добавить.",
            "RegisterHook(\"/Script/SCUM.PlayerQuestComponent:Server_StartQuest\", function(self)\n  print(\"quest started\\n\")\nend)",
            "/Script/SCUM.PlayerQuestComponent:Server_StartQuest"),
        new("Hook.Purchase", "Hooks", LuaApiKind.Hook, "Server_PlayerPurchaseTradeable", "RegisterHook(\"" + Rpc + "Server_PlayerPurchaseTradeable\", function(self) end)", LuaApiStatus.Seen,
            "A player buys from a trader. Reading works. Changing clientsidePlayerBasedPriceMultiplier does not: the server compares it with its own value and refuses the purchase.",
            "Игрок покупает у торговца. Чтение работает. Изменение clientsidePlayerBasedPriceMultiplier - нет: сервер сверяет его со своим и отклоняет покупку.",
            "RegisterHook(\"" + Rpc + "Server_PlayerPurchaseTradeable\", function(self)\n  print(\"purchase\\n\")\nend)",
            Rpc + "Server_PlayerPurchaseTradeable"),

        // ---- Safety: rules and dead ends ----
        new("Rule.NoPlayerInHook", "Safety", LuaApiKind.Rule, "Do not touch a player inside a hook", "", LuaApiStatus.Tested,
            "A native call on a player inside a hook callback crashed the server (the player is not ready yet). Remember it, then ExecuteWithDelay(800+, ...) and ExecuteInGameThread.",
            "Обращение к игроку внутри хука роняло сервер (игрок ещё не готов). Запомните его, затем ExecuteWithDelay(800+, ...) и ExecuteInGameThread.",
            "ExecuteWithDelay(800, function()\n  ExecuteInGameThread(function()\n    -- now the player may be used\n  end)\nend)"),
        new("Rule.IsValid", "Safety", LuaApiKind.Rule, "Always check :IsValid()", "", LuaApiStatus.Tested,
            "FindFirstOf returns a truthy but dead object when nothing is found; using it crashes. Check obj and obj:IsValid() every time.",
            "FindFirstOf при промахе возвращает «живую на вид» пустышку, обращение к ней роняет игру. Проверяйте obj и obj:IsValid() каждый раз.",
            "if obj and obj:IsValid() then\n  -- safe\nend"),
        new("Rule.HeavyLoop", "Safety", LuaApiKind.Rule, "No heavy FindAllOf in a loop", "", LuaApiStatus.Tested,
            "FindAllOf over many classes froze the game thread for 20 s and the watchdog killed the server. Look at players (BP_Prisoner_C) only, at most every few seconds, and spawn at most 2 actors per tick.",
            "FindAllOf по многим классам вешал игровой поток на 20 с, сервер убил сторож. Смотрите только игроков (BP_Prisoner_C), не чаще раза в несколько секунд, и создавайте не больше 2 акторов за тик.",
            string.Empty),
        new("Rule.SoftPointer", "Safety", LuaApiKind.Rule, "Never write a soft class pointer", "deal.TradeableClass = ...", LuaApiStatus.Unsafe,
            "Writing a class or a path string into a soft pointer field (TradeableClass of a trade deal) crashed the server. Numbers and booleans in live data can be written; soft pointers can only be read or copied.",
            "Запись класса или строки-пути в поле мягкой ссылки (TradeableClass торговой сделки) роняла сервер. Числа и флаги можно писать; мягкие ссылки - только читать или копировать.",
            string.Empty),
        new("Rule.Format", "Safety", LuaApiKind.Rule, "string.format %d needs an integer", string.Empty, LuaApiStatus.Tested,
            "The game's numbers are floats; %d on a float fails. Use %.0f or math.floor.",
            "Числа игры - дробные; %d на дробном числе падает. Берите %.0f или math.floor.",
            "string.format(\"%.0f\", value)"),
        new("Rule.NoItems", "Safety", LuaApiKind.Rule, "Items for non-admins cannot be spawned", string.Empty, LuaApiStatus.Unsafe,
            "SpawnActor of an item, MiscStatics:Test_ProcessAdminCommand and an IsUserAdmin hook did not give items to a player who is not an admin; the chat command SpawnItem answers \"Not authorized\".",
            "SpawnActor предмета, MiscStatics:Test_ProcessAdminCommand и хук IsUserAdmin не дали предмет игроку без админки; команда SpawnItem отвечает «Not authorized».",
            string.Empty),
        new("Rule.HideChat", "Safety", LuaApiKind.Rule, "A chat line cannot be hidden", "Message:set(...)", LuaApiStatus.Unsafe,
            "Changing the Message argument in the chat hook returns true but the text is still shown to everybody. For a private answer use SendHUDMessageToClient (one line).",
            "Изменение аргумента Message в хуке чата возвращает true, но текст всё равно виден всем. Для личного ответа используйте SendHUDMessageToClient (одна строка).",
            string.Empty),
        new("Rule.Reload", "Safety", LuaApiKind.Rule, "Saving reloads a running mod; a new mod needs a restart", string.Empty, LuaApiStatus.Tested,
            "UE4SS reloads a mod when its main.lua is saved (sometimes it keeps the old code: save once more). A mod that was not loaded at the server start is loaded only after a restart.",
            "UE4SS перезагружает мод при сохранении main.lua (иногда остаётся старый код: сохраните ещё раз). Мод, которого не было при старте сервера, загрузится только после перезапуска.",
            string.Empty),

        // ---- Recipes ----
        new("Recipe.Log", "Recipes", LuaApiKind.Recipe, "Write a line to a log file", "io.open(\"ue4ss/my.log\", \"a\")", LuaApiStatus.Tested,
            "Files are written relative to the Win64 folder of the server, so ue4ss/... lands next to UE4SS.log.",
            "Файлы пишутся относительно папки Win64 сервера, поэтому ue4ss/... ложится рядом с UE4SS.log.",
            "local function log(text)\n  local f = io.open(\"ue4ss/my_mod.log\", \"a\")\n  if f then f:write(os.date(\"%Y-%m-%d %H:%M:%S \"), text, \"\\n\"); f:close() end\nend"),
        new("Recipe.NearPlayer", "Recipes", LuaApiKind.Recipe, "Is a player within a radius", "FindAllOf(\"BP_Prisoner_C\") + K2_GetActorLocation", LuaApiStatus.Tested,
            "The base of the guard mod: act only while somebody is near a place, so an empty server costs nothing.",
            "Основа мода охраны: действовать, только пока рядом кто-то есть, чтобы пустой сервер ничего не стоил.",
            "local function playerNear(cx, cy, radius)\n  local ok, list = pcall(FindAllOf, \"BP_Prisoner_C\")\n  if not ok or type(list) ~= \"table\" then return false end\n  for _, p in ipairs(list) do\n    if p:IsValid() then\n      local okl, at = pcall(function() return p:K2_GetActorLocation() end)\n      if okl and at then\n        local dx, dy = at.X - cx, at.Y - cy\n        if dx * dx + dy * dy <= radius * radius then return true end\n      end\n    end\n  end\n  return false\nend"),
        new("Recipe.SpawnGuard", "Recipes", LuaApiKind.Recipe, "Spawn an armed guard", "world:SpawnActor(BP_Guard_Lvl_N_C, ...)", LuaApiStatus.Tested,
            "An armed NPC of level 3 (weak) to 5 (strong). The game gives it an AI by itself. For many posts use the Guard posts tool of the Map page instead.",
            "Вооружённый NPC уровня 3 (слабый) - 5 (сильный). ИИ игра даёт сама. Для многих постов лучше инструмент «Посты охраны» на странице Карта.",
            "local FOLDER = \"/Game/ConZ_Files/Characters/NPCs/Armed_NPCs/Blueprint/Guard/\"\nlocal function guardClass(level)\n  local name = \"BP_Guard_Lvl_\" .. level\n  local full = FOLDER .. name .. \".\" .. name .. \"_C\"\n  local c = StaticFindObject(full)\n  if not (c and c:IsValid()) then\n    pcall(function() LoadAsset(FOLDER .. name .. \".\" .. name) end)\n    c = StaticFindObject(full)\n  end\n  return c and c:IsValid() and c or nil\nend"),
    ];

    /// <summary>The entry text in the given language (Russian for "ru", English otherwise).</summary>
    public static string TextOf(LuaApiEntry entry, string language) =>
        string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase) ? entry.TextRu : entry.Text;
}
