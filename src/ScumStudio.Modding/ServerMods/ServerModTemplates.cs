namespace ScumStudio.Modding.ServerMods;

/// <summary>A ready Lua mod for the server.</summary>
/// <param name="Id">Stable key, also the suggested mod name.</param>
/// <param name="Code">The <c>main.lua</c> text; the lines under SETTINGS are the ones to change.</param>
public sealed record ServerModTemplate(string Id, string Code);

/// <summary>
/// Starting mods for the server. Every game function they call was found in the running SCUM Server (class and function dump of
/// 2026-10-25) and called there once without an error: <c>ConZGameMode:SendHUDMessageToAll(Message, beep)</c>, the player hooks
/// <c>PlayerRpcChannel:SurvivalStats_Server_HandlePlayerLogin / HandlePlayerLogout</c> and <c>PlayerRpcChannel:Prisoner_Server_Die</c>.
/// </summary>
public static class ServerModTemplates
{
    /// <summary>All templates, easiest first.</summary>
    public static IReadOnlyList<ServerModTemplate> All { get; } =
    [
        new("Announcer", """
            -- ANNOUNCER / ОБЪЯВЛЕНИЯ: shows a message on every player's screen every few minutes.
            -- SETTINGS / НАСТРОЙКИ (change the numbers and the texts in quotes / меняйте числа и тексты в кавычках)
            local FIRST_AFTER_MINUTES = 5     -- first message after the server starts / первое сообщение после старта
            local EVERY_MINUTES = 30          -- then every ... minutes / потом каждые ... минут
            local MESSAGES = {                -- the messages, one after another / сообщения по очереди
              "Welcome to the server! Be kind to other players.",
              "Need help or found a bug? Tell an admin.",
            }

            -- Below: no need to change / Ниже менять не нужно
            local index = 0
            local function say(text)
              ExecuteInGameThread(function()
                local gm = FindFirstOf("BP_ConZGameMode_C")
                if gm and gm:IsValid() then pcall(function() gm:SendHUDMessageToAll(text, false) end) end
              end)
            end

            ExecuteWithDelay(FIRST_AFTER_MINUTES * 60000, function()
              LoopAsync(EVERY_MINUTES * 60000, function()
                index = index % #MESSAGES + 1
                say(MESSAGES[index])
                return false
              end)
            end)
            print("[Announcer] on, every " .. EVERY_MINUTES .. " min\n")
            """),
        new("Welcome", """
            -- WELCOME / ПРИВЕТСТВИЕ: tells everyone when a player joins the server.
            -- SETTINGS / НАСТРОЙКИ
            local TEXT = "%s joined the server. Welcome!"   -- %s is the player's name / %s - имя игрока
            local SHOW_LEAVE = false                         -- also say when somebody leaves / писать и о выходе
            local LEAVE_TEXT = "%s left the server."
            local CHECK_SECONDS = 10                         -- how often the server is looked at / как часто смотреть

            -- Below: no need to change / Ниже менять не нужно
            -- The join and leave hooks only ask for a look at the player list a moment later: reading a player inside the hook
            -- crashed the server (a native call on a player that is not ready yet).
            local known, first = {}, true
            local function nameOf(pc)
              local ok, name = pcall(function()
                local state = pc.PlayerState
                if state and state:IsValid() then
                  local n = state.PlayerNamePrivate
                  if n then return n:ToString() end
                end
              end)
              if ok and type(name) == "string" and name ~= "" then return name end
              return "A player"
            end
            local function say(text)
              local gm = FindFirstOf("BP_ConZGameMode_C")
              if gm and gm:IsValid() then pcall(function() gm:SendHUDMessageToAll(text, false) end) end
            end
            local function scan()
              local ok, list = pcall(FindAllOf, "BP_ConZPlayerController_C")
              if not ok or type(list) ~= "table" then return end
              local seen = {}
              for _, pc in ipairs(list) do
                if pc:IsValid() then
                  local id = pc:GetFullName()
                  seen[id] = true
                  if not known[id] then
                    known[id] = nameOf(pc)
                    if not first then say(string.format(TEXT, known[id])) end
                  end
                end
              end
              for id, name in pairs(known) do
                if not seen[id] then
                  known[id] = nil
                  if SHOW_LEAVE and not first then say(string.format(LEAVE_TEXT, name)) end
                end
              end
              first = false
            end
            local function scanSoon() ExecuteWithDelay(3000, function() ExecuteInGameThread(scan) end) end

            RegisterHook("/Script/SCUM.PlayerRpcChannel:SurvivalStats_Server_HandlePlayerLogin", scanSoon)
            RegisterHook("/Script/SCUM.PlayerRpcChannel:SurvivalStats_Server_HandlePlayerLogout", scanSoon)
            ExecuteWithDelay(30000, function()
              ExecuteInGameThread(scan)
              LoopAsync(CHECK_SECONDS * 1000, function() ExecuteInGameThread(scan); return false end)
            end)
            print("[Welcome] on\n")
            """),
        new("EventLog", """
            -- EVENT LOG / ЖУРНАЛ СОБЫТИЙ: writes who joined and left, and when somebody died, with the time, to ue4ss/server_events.log.
            -- SETTINGS / НАСТРОЙКИ
            local FILE = "ue4ss/server_events.log"     -- the log file (next to UE4SS.log) / файл журнала
            local LOG_JOIN, LOG_LEAVE, LOG_DEATH = true, true, true
            local CHECK_SECONDS = 10                   -- how often the server is looked at / как часто смотреть

            -- Below: no need to change / Ниже менять не нужно
            -- The hooks only ask for a look at the player list a moment later: reading a player inside a hook crashed the server
            -- (a native call on a player that is not ready yet). A death is logged without a name for the same reason.
            local known, first = {}, true
            local function log(kind, who)
              local file = io.open(FILE, "a")
              if file then
                file:write(os.date("%Y-%m-%d %H:%M:%S"), "  ", kind, "  ", who or "", "\n")
                file:close()
              end
            end
            local function nameOf(pc)
              local ok, name = pcall(function()
                local state = pc.PlayerState
                if state and state:IsValid() then
                  local n = state.PlayerNamePrivate
                  if n then return n:ToString() end
                end
              end)
              if ok and type(name) == "string" and name ~= "" then return name end
              return pc:GetFullName()
            end
            local function scan()
              local ok, list = pcall(FindAllOf, "BP_ConZPlayerController_C")
              if not ok or type(list) ~= "table" then return end
              local seen = {}
              for _, pc in ipairs(list) do
                if pc:IsValid() then
                  local id = pc:GetFullName()
                  seen[id] = true
                  if not known[id] then
                    known[id] = nameOf(pc)
                    if LOG_JOIN then log(first and "ONLINE" or "JOIN  ", known[id]) end
                  end
                end
              end
              for id, name in pairs(known) do
                if not seen[id] then
                  known[id] = nil
                  if LOG_LEAVE then log("LEAVE ", name) end
                end
              end
              first = false
            end
            local function scanSoon() ExecuteWithDelay(3000, function() ExecuteInGameThread(scan) end) end

            RegisterHook("/Script/SCUM.PlayerRpcChannel:SurvivalStats_Server_HandlePlayerLogin", scanSoon)
            RegisterHook("/Script/SCUM.PlayerRpcChannel:SurvivalStats_Server_HandlePlayerLogout", scanSoon)
            if LOG_DEATH then
              RegisterHook("/Script/SCUM.PlayerRpcChannel:Prisoner_Server_Die", function() log("DEATH ", "a player died") end)
            end
            ExecuteWithDelay(30000, function()
              ExecuteInGameThread(scan)
              LoopAsync(CHECK_SECONDS * 1000, function() ExecuteInGameThread(scan); return false end)
            end)
            print("[EventLog] writing to " .. FILE .. "\n")
            """),
        new("TimeCommand", """
            -- TIME COMMAND / КОМАНДА ВРЕМЕНИ: a player writes !time in the chat and gets the game time of the server, only to him.
            -- SETTINGS / НАСТРОЙКИ
            local COMMAND = "!time"                 -- what the player writes / что пишет игрок
            local TEXT = "Game time %s"             -- %s is the time, like 20:51 / %s - время, например 20:51

            -- Below: no need to change / Ниже менять не нужно
            -- The hook only remembers who wrote; the answer is sent a moment later, never inside the hook (a native call there can crash the server).
            -- Not "#time": the game takes everything that starts with # as its own admin command.
            local function gameTime()
              local gm = FindFirstOf("BP_ConZGameMode_C")
              local ws = StaticFindObject("/Script/SCUM.Default__WorldStatics")
              local ok, hours = pcall(function() return ws:GetTimeOfDay(gm) end)
              if not ok or type(hours) ~= "number" then return nil end
              local h = math.floor(hours)
              return string.format("%02d:%02d", h, math.floor((hours - h) * 60))
            end

            RegisterHook("/Script/SCUM.PlayerRpcChannel:Chat_Server_BroadcastChatMessage", function(self, Message, Channel)
              local ok, text = pcall(function() return Message:get():ToString() end)
              if not ok or type(text) ~= "string" then return end
              if string.lower(text:match("^%s*(.-)%s*$")) ~= string.lower(COMMAND) then return end
              local channel
              pcall(function() channel = self:get() end)
              ExecuteWithDelay(800, function()
                ExecuteInGameThread(function()
                  if not (channel and channel:IsValid()) then return end
                  local player = channel:GetOuter()
                  local now = gameTime()
                  if player and player:IsValid() and now then
                    pcall(function() player:SendHUDMessageToClient(string.format(TEXT, now), nil) end)
                  end
                end)
              end)
            end)
            print("[TimeCommand] on, write " .. COMMAND .. " in the chat\n")
            """),
        new("Dump", """
            -- CLASS DUMP / СПИСОК КЛАССОВ: writes the SCUM classes and functions the server has loaded to ue4ss/scum_dump.txt.
            -- Use it to find the names for your own mods. Changes nothing in the game / Ничего не меняет в игре.
            -- SETTINGS / НАСТРОЙКИ
            local DELAY_SECONDS = 120          -- wait until the server has loaded / ждать загрузки сервера
            local OUT = "ue4ss/scum_dump.txt"
            local WANT = { "/Script/SCUM", "/Game/ConZ_Files" }   -- keep only names that contain one of these / только эти

            -- Below: no need to change / Ниже менять не нужно
            local function wanted(name)
              for _, key in ipairs(WANT) do
                if string.find(name, key, 1, true) then return true end
              end
              return false
            end

            local function dump()
              local file = io.open(OUT, "w")
              if not file then print("[Dump] cannot open " .. OUT .. "\n"); return end
              local counts = {}
              for _, className in ipairs({ "Class", "BlueprintGeneratedClass", "Function" }) do
                local n = 0
                local ok, list = pcall(FindObjects, 0, className, "", 0, 0, false)
                if ok and type(list) == "table" then
                  for _, object in ipairs(list) do
                    local ok2, full = pcall(function() return object:GetFullName() end)
                    if ok2 and type(full) == "string" and wanted(full) then file:write(full, "\n"); n = n + 1 end
                  end
                end
                counts[#counts + 1] = className .. "=" .. n
                file:flush()
              end
              file:close()
              print("[Dump] wrote " .. OUT .. ": " .. table.concat(counts, ", ") .. "\n")
            end

            print("[Dump] loaded; dumping in " .. DELAY_SECONDS .. " s\n")
            ExecuteWithDelay(DELAY_SECONDS * 1000, function()
              ExecuteInGameThread(function()
                local ok, err = xpcall(dump, function(e) return tostring(e) end)
                if not ok then print("[Dump] error: " .. tostring(err) .. "\n") end
              end)
            end)
            """),
    ];
}
