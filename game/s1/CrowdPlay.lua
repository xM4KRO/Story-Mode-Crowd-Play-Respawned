-- Local Crowd Play bridge for Minecraft: Story Mode.
-- The game supplies choice events and renders its own menus.  A local helper
-- serves the phone page and counts votes on the player's LAN.

Callback_CrowdPlayEndSession = Callback(true)
Callback_CrowdPlayRefresh = Callback(true)
Callback_CrowdPlayServerHealth = Callback(true)
Callback_CrowdPlayNumPlayers = Callback(true)
Callback_CrowdPlayResponse = Callback(true)
Callback_CrowdPlayFinalResponse = Callback(true)

local kBase = "http://127.0.0.1:17861"
local kSessionCode = "Crowd Play - Session Code"
local kUseGamePrefs = false
local mRoomCode = ""
local mJoinURL = ""
local mNetworkMode = "LOCAL"
local mNetworkStatus = "READY"
local mPlayers = 0
local mServerHealthy = false
local mHostEnabled = false
local mReportedPaused = nil
local mStatusThread = nil
local mVoteThread = nil
local mVoteActive = false
local mVoteDenouement = false
local mPollID = ""
local mSequence = 0
local mCounts = {}
local mChoiceCount = 0
local mWinner = 0
local mHostVote = 0
local mFinalFired = false
local mHostDecides = false
local mOverlay = true
local mShowCounts = true
local mManualPollID = ""
local mManualPollActive = false
local mPollSummary = "No vote in progress"
local mPollCountsText = ""

local function Parts(value)
  local out = {}
  local start = 1
  value = tostring(value or "")
  while true do
    local stop = string.find(value, "|", start, true)
    if not stop then
      out[#out + 1] = string.sub(value, start)
      break
    end
    out[#out + 1] = string.sub(value, start, stop - 1)
    start = stop + 1
  end
  return out
end

local function Escape(value)
  return (string.gsub(tostring(value or ""), "[^%w%-._~]", function(byte)
    return string.format("%%%02X", string.byte(byte))
  end))
end

local function Request(path)
  local ok, body = pcall(function()
    -- This exact five-argument form is used by the shipped Season Two script.
    return HttpPostAndWait(kBase .. path, "", 1, {}, true)
  end)
  if ok and type(body) == "string" and body ~= "" then
    if not mServerHealthy then
      mServerHealthy = true
      Callback_CrowdPlayServerHealth:Run(true)
    end
    return Parts(body)
  end
  if mServerHealthy then
    mServerHealthy = false
    Callback_CrowdPlayServerHealth:Run(false)
  end
  return nil
end

local function ReadConfig()
  local response = Request("/control/config")
  if response and response[1] == "CONFIG" then
    mHostDecides = response[2] == "1"
    mOverlay = response[3] ~= "0"
    if response[4] then
      mShowCounts = response[4] ~= "0"
    end
  end
end

local function ReportPauseState(force)
  if not CrowdPlay_IsEnabled() then return false end
  local paused = Game_IsPaused() and true or false
  if not force and mReportedPaused == paused then return true end
  local response = Request("/control/pause?value=" .. (paused and "1" or "0"))
  if response and response[1] == "PAUSED" and response[2] == (paused and "1" or "0") then
    mReportedPaused = paused
    return true
  end
  return false
end

local function RefreshPlayers()
  local response = Request("/control/status")
  if not response or response[1] ~= "READY" then
    return false
  end
  local desiredEnabled = CrowdPlay_IsEnabled()
  local hostEnabled = response[5] == "1"
  if hostEnabled ~= desiredEnabled then
    local target = desiredEnabled and "1" or "0"
    local updated = Request("/control/enabled?value=" .. target)
    if not updated or updated[1] ~= "ENABLED" or updated[2] ~= target then
      mHostEnabled = false
      return false
    end
    hostEnabled = desiredEnabled
    mReportedPaused = nil
  end
  mHostEnabled = hostEnabled
  local oldJoinURL = mJoinURL
  local oldMode = mNetworkMode
  local oldStatus = mNetworkStatus
  -- Fields six and seven are appended by the host. The original five fields
  -- remain in place for older game scripts.
  mNetworkMode = response[6] == "ONLINE" and "ONLINE" or "LOCAL"
  mNetworkStatus = response[7] or "READY"
  mJoinURL = response[3] or ""
  local currentRoom = response[2]
  if currentRoom and currentRoom ~= "" then
    mRoomCode = currentRoom
    -- The host creates a new room when it restarts. Keep the game's saved
    -- code in step so a menu never advertises an expired join link.
    if CrowdPlay_IsEnabled() and SessionProperties_Get(kSessionCode) ~= currentRoom then
      SessionProperties_Set(kSessionCode, currentRoom, "String")
      Callback_CrowdPlayRefresh:Run()
    end
  end
  local newCount = hostEnabled and (tonumber(response[4]) or 0) or 0
  if mPlayers ~= newCount then
    mPlayers = newCount
    local players = {}
    for i = 1, mPlayers do
      players[tostring(i)] = {
        id = tostring(i),
        uuid = tostring(i),
        name = "Player " .. tostring(i),
        decorator = "1",
        thumbsup = 0,
        thumbsdown = 0
      }
    end
    Callback_CrowdPlayNumPlayers:Run(mPlayers, players, false, nil)
  end
  if CrowdPlay_IsEnabled() and
      (oldJoinURL ~= mJoinURL or oldMode ~= mNetworkMode or
       oldStatus ~= mNetworkStatus) then
    Callback_CrowdPlayRefresh:Run()
  end
  return true
end

local function StatusLoop()
  -- Menu.lua uses GetTotalTime for its own input timing, so this clock keeps
  -- advancing while the player is in a menu or the game is paused.
  local nextRefresh = GetTotalTime() + 1
  local nextPauseAttempt = 0
  while CrowdPlay_IsEnabled() do
    local now = GetTotalTime()
    if now >= nextPauseAttempt and mReportedPaused ~= (Game_IsPaused() and true or false) then
      nextPauseAttempt = now + 1
      ReportPauseState(false)
    end
    if now >= nextRefresh then
      nextRefresh = now + 1
      RefreshPlayers()
    end
    WaitForNextFrame()
  end
  mStatusThread = nil
end

local function ReadCounts(csv)
  local changed = false
  local index = 1
  for value in string.gmatch(csv or "", "[^,]+") do
    local count = tonumber(value) or 0
    if mCounts[index] ~= count then
      mCounts[index] = count
      changed = true
    end
    index = index + 1
  end
  return changed
end

local function BestResponse(serverWinner)
  local best = 0
  local tied = {}
  for i = 1, mChoiceCount do
    local count = (mCounts[i] or 0) + (mHostVote == i and 1 or 0)
    if count > best then
      best = count
      tied = {i}
    elseif count == best and count > 0 then
      tied[#tied + 1] = i
    end
  end
  if best < 1 then
    return 0
  end
  if #tied == 1 then
    return tied[1]
  end
  for _, index in ipairs(tied) do
    if index == serverWinner then
      return index
    end
  end
  return tied[math.random(1, #tied)]
end

local function UpdateVote(response)
  if not response or (response[1] ~= "ACTIVE" and response[1] ~= "ENDED") then
    return
  end
  if response[2] ~= mPollID then
    return
  end
  local changed = ReadCounts(response[3])
  local serverWinner = tonumber(response[4]) or 0
  if changed then
    Callback_CrowdPlayResponse:Run()
  end
  if response[1] == "ENDED" then
    mWinner = BestResponse(serverWinner)
  end
  if response[1] == "ENDED" and mWinner > 0 and not mFinalFired then
    mFinalFired = true
    Callback_CrowdPlayFinalResponse:Run(mWinner)
  end
end

local function VoteLoop()
  local elapsed = 0
  local activePoll = mPollID
  while mVoteActive and activePoll == mPollID and CrowdPlay_IsEnabled() do
    if elapsed >= 0.2 then
      elapsed = 0
      local response = Request("/control/state")
      if mVoteActive and activePoll == mPollID then
        UpdateVote(response)
      end
    end
    WaitForNextFrame()
    elapsed = elapsed + GetFrameTime()
  end
  mVoteThread = nil
end

function CrowdPlay_GetVersion()
  return "local-1"
end

function CrowdPlay_IsSupported()
  return true
end

function CrowdPlay_IsSocketsSupported()
  return true
end

function CrowdPlay_IsConnected()
  return mServerHealthy and mHostEnabled
end

function CrowdPlay_IsActivityAllowed()
  return mServerHealthy and mHostEnabled, true,
    "popup_crowdPlayNetworkDisconnect_header",
    "popup_crowdPlayNetworkDisconnect_message"
end

function CrowdPlay_RequestSession()
  if CrowdPlay_IsEnabled() then
    return CrowdPlay_GetSessionCode(), "AlreadyEnabled"
  end
  if not RefreshPlayers() then
    return nil, "LocalHostUnavailable"
  end
  if mRoomCode == "" then
    return nil, "NoRoomCode"
  end
  local enabled = Request("/control/enabled?value=1")
  if not enabled or enabled[1] ~= "ENABLED" or enabled[2] ~= "1" then
    return nil, "LocalHostUnavailable"
  end
  mHostEnabled = true
  mReportedPaused = nil
  SessionProperties_Set(kSessionCode, mRoomCode, "String")
  ReportPauseState(true)
  ReadConfig()
  Callback_CrowdPlayRefresh:Run()
  if not mStatusThread then
    mStatusThread = ThreadStart(StatusLoop)
  end
  return mRoomCode, nil
end

function CrowdPlay_GetSessionCode()
  local stored = SessionProperties_Get(kSessionCode)
  if (not mRoomCode or mRoomCode == "") and stored and stored ~= "" then
    mRoomCode = stored
  end
  return mRoomCode
end

function CrowdPlay_GetSessionServer()
  return "127.0.0.1", "17861"
end

function CrowdPlay_IsEnabled()
  return (SessionProperties_Get(kSessionCode) or "") ~= ""
end

function CrowdPlay_EndSession()
  if mVoteActive then
    CrowdPlay_EndChoice()
  end
  if mManualPollActive then
    CrowdPlay_CloseManualPoll()
  end
  mManualPollActive = false
  mManualPollID = ""
  mRoomCode = ""
  mJoinURL = ""
  mNetworkMode = "LOCAL"
  mNetworkStatus = "READY"
  mPlayers = 0
  Request("/control/enabled?value=0")
  mHostEnabled = false
  mReportedPaused = nil
  SessionProperties_Set(kSessionCode, "", "String")
  Callback_CrowdPlayEndSession:Run()
  Callback_CrowdPlayRefresh:Run()
end

function CrowdPlay_GetNumPlayers()
  return mPlayers + (CrowdPlay_IsEnabled() and 1 or 0)
end

function CrowdPlay_GetNumServerPlayers()
  return mPlayers
end

function CrowdPlay_GetNumHostPlayers()
  return CrowdPlay_IsEnabled() and 1 or 0
end

function CrowdPlay_GetServerPlayers()
  local players = {}
  for i = 1, mPlayers do
    players[tostring(i)] = {
      id = tostring(i), name = "Player " .. tostring(i),
      decorator = "1", thumbsup = 0, thumbsdown = 0
    }
  end
  return players
end

function CrowdPlay_GetSmallCrowdLimit()
  return 50
end

function CrowdPlay_GetSessionCrowdMax()
  return 50
end

function CrowdPlay_ReportCrowdSize()
end

function CrowdPlay_IsStatsEnabled()
  return CrowdPlay_IsEnabled()
end

function CrowdPlay_GetServerHealthy()
  return mServerHealthy
end

function CrowdPlay_SetServerHealthy(healthy)
  mServerHealthy = healthy
  Callback_CrowdPlayServerHealth:Run(healthy)
end

function CrowdPlay_IsExpressionAllowed()
  return false
end

function CrowdPlay_StopAcceptingPlayers()
end

function CrowdPlay_AddExpressionCount()
end

function CrowdPlay_GetExpressionCount()
  return 0
end

function CrowdPlay_SendExpressionCounts()
end

function CrowdPlay_IsVoteActive()
  return mVoteActive
end

function CrowdPlay_BeginChoice(choiceNodeID, optionTable, remainingSeconds)
  if type(choiceNodeID) == "table" then
    remainingSeconds = optionTable
    optionTable = choiceNodeID
    choiceNodeID = nil
  end
  if not CrowdPlay_IsEnabled() or mVoteActive then
    return false
  end
  if mManualPollActive then
    CrowdPlay_CloseManualPoll()
  end
  optionTable = optionTable or {}
  if #optionTable < 2 then
    return false
  end
  mSequence = mSequence + 1
  mPollID = tostring(choiceNodeID or "choice") .. "-" .. tostring(mSequence)
  mChoiceCount = #optionTable
  mCounts = {}
  for i = 1, mChoiceCount do
    mCounts[i] = 0
  end
  mHostVote = 0
  mWinner = 0
  mFinalFired = false
  local duration = tonumber(remainingSeconds) or 20
  duration = math.max(1, math.min(duration, 90))
  ReportPauseState(true)
  local url = "/control/start?poll=" .. Escape(mPollID)
    .. "&duration_ms=" .. tostring(math.floor(duration * 1000))
  for _, option in ipairs(optionTable) do
    url = url .. "&choice=" .. Escape(option.text or "")
  end
  local response = Request(url)
  if not response or response[1] ~= "STARTED" then
    return false
  end
  mVoteActive = true
  mVoteDenouement = false
  Callback_CrowdPlayRefresh:Run()
  mVoteThread = ThreadStart(VoteLoop)
  return true
end

function CrowdPlay_SetChoiceInitState()
end

function CrowdPlay_ReBeginChoice()
  -- The LAN poll remains active while the game is paused.
end

function CrowdPlay_SetLocalPlayerResponse(index)
  index = tonumber(index) or 0
  if mVoteActive and index >= 1 and index <= mChoiceCount then
    mHostVote = index
    Request("/control/host-vote?poll=" .. Escape(mPollID) .. "&choice=" .. tostring(index))
    Callback_CrowdPlayResponse:Run()
  end
end

function CrowdPlay_GetHostResponse()
  return mHostVote
end

function CrowdPlay_GetResponses()
  local players = {}
  local represented = 0
  for i = 1, mChoiceCount do
    local count = mCounts[i] or 0
    for j = 1, count do
      if represented >= 50 then
        break
      end
      represented = represented + 1
      local id = tostring(i) .. "-" .. tostring(j)
      players[id] = {
        id = id, decorator = "1", name = "", votedfor = i
      }
    end
  end
  return {
    controllers = {{id = 1, decorator = "0", name = "Host", votedfor = mHostVote}},
    players = players
  }
end

function CrowdPlay_GetServerResponses()
  local results = {}
  for i = 1, mChoiceCount do
    results[i] = {votecount = mCounts[i] or 0}
  end
  return results
end

function CrowdPlay_SetServerResponses(responses)
  for i = 1, mChoiceCount do
    mCounts[i] = responses and responses[i] and responses[i].votecount or 0
  end
end

function CrowdPlay_GetNumResponses(index)
  if index then
    return (mCounts[index] or 0) + (mHostVote == index and 1 or 0)
  end
  local total = mHostVote > 0 and 1 or 0
  for i = 1, mChoiceCount do
    total = total + (mCounts[i] or 0)
  end
  return total
end

function CrowdPlay_GetSelectedResponse(bEndChoice)
  if not mVoteActive then
    return nil
  end
  local response = Request("/control/end?poll=" .. Escape(mPollID))
  if response and response[1] == "ENDED" then
    if response[3] then
      ReadCounts(response[3])
    end
    mWinner = BestResponse(tonumber(response[2]) or 0)
  else
    -- Keep the latest known votes if the local host becomes unavailable at
    -- the exact moment the game's choice timer expires.
    mWinner = BestResponse(mWinner)
  end
  local selected = mWinner > 0 and mWinner or nil
  if bEndChoice ~= false then
    CrowdPlay_EndChoice()
  end
  return selected
end

function CrowdPlay_EndChoice()
  if not mVoteActive then
    return
  end
  mVoteActive = false
  Request("/control/end?poll=" .. Escape(mPollID))
  Callback_CrowdPlayRefresh:Run()
end

function CrowdPlay_ChoiceSetDenouement(value)
  mVoteDenouement = value and true or false
end

function CrowdPlay_EnterPostEpisode()
end

function CrowdPlay_GetContextForEpisode()
  return ""
end

function CrowdPlay_RequestCrowdStats()
  return nil
end

function CrowdPlay_HostMakesChoice()
  if kUseGamePrefs then
    local prefs = GetPreferences()
    local stored = prefs and prefs["Crowd Play - Host Makes Choice"]
    if stored ~= nil then
      return stored and true or false
    end
  end
  return mHostDecides
end

function CrowdPlay_SetHostMakesChoice(enabled)
  mHostDecides = enabled and true or false
  Request("/control/config?host_decides=" .. (mHostDecides and "1" or "0"))
end

function CrowdPlay_GetOverlayEnabled()
  return mOverlay
end

function CrowdPlay_SetOverlayEnabled(enabled)
  mOverlay = enabled and true or false
  Request("/control/config?overlay=" .. (mOverlay and "1" or "0"))
end

function CrowdPlay_GetShowCounts()
  return mShowCounts
end

function CrowdPlay_SetShowCounts(enabled)
  local response = Request("/control/config?show_counts=" .. (enabled and "1" or "0"))
  if response and response[1] == "CONFIG" then
    mShowCounts = response[4] ~= "0"
    return true
  end
  return false
end

function CrowdPlay_RefreshInfo()
  local ready = RefreshPlayers()
  if ready then ReadConfig() end
  if ready and CrowdPlay_IsEnabled() and not mStatusThread then
    mStatusThread = ThreadStart(StatusLoop)
  end
  return ready
end

function CrowdPlay_GetNetworkMode()
  return mNetworkMode
end

function CrowdPlay_GetNetworkStatus()
  return mNetworkStatus
end

function CrowdPlay_SetNetworkMode(mode)
  if not CrowdPlay_IsEnabled() or (mode ~= "LOCAL" and mode ~= "ONLINE") then
    return false
  end
  local response = Request("/control/mode?value=" .. string.lower(mode))
  if not response or response[1] ~= "MODE" or response[2] ~= mode then
    return false
  end
  mNetworkMode = mode
  mNetworkStatus = response[3] or "PENDING"
  -- The advertised link is owned by the host. It may be temporarily empty
  -- while a tunnel starts, so never show or copy the old LAN link in Online.
  mJoinURL = ""
  RefreshPlayers()
  Callback_CrowdPlayRefresh:Run()
  return true
end

function CrowdPlay_Copy(kind)
  if kind ~= "room" and kind ~= "address" and kind ~= "link" then
    return false
  end
  if kind ~= "room" and CrowdPlay_GetJoinURL() == "" then
    return false
  end
  local response = Request("/control/copy?kind=" .. kind)
  return response and response[1] == "COPIED" or false
end

function CrowdPlay_SaveReport()
  local response = Request("/control/report")
  return response and response[1] == "SAVED" or false
end

function CrowdPlay_NewRoom()
  if mVoteActive then
    return nil, "StoryVoteActive"
  end
  local response = Request("/control/new-room")
  if not response or response[1] ~= "NEWROOM" or not response[2] or response[2] == "" then
    return nil, "NewRoomFailed"
  end
  local enabled = CrowdPlay_IsEnabled()
  mRoomCode = response[2]
  mJoinURL = response[3] or ""
  mPlayers = 0
  mManualPollID = ""
  mManualPollActive = false
  if enabled then
    SessionProperties_Set(kSessionCode, mRoomCode, "String")
  end
  Callback_CrowdPlayNumPlayers:Run(0, {}, false, nil)
  Callback_CrowdPlayRefresh:Run()
  return mRoomCode, nil
end

function CrowdPlay_StartManualPoll(choices, durationSeconds)
  if mVoteActive then
    return nil, "StoryVoteActive"
  end
  if type(choices) ~= "table" or #choices < 2 or #choices > 4 then
    return nil, "InvalidChoices"
  end
  local duration = tonumber(durationSeconds)
  if not duration or duration < 5 or duration > 120 then
    return nil, "InvalidDuration"
  end
  for _, choice in ipairs(choices) do
    if type(choice) ~= "string" or choice == "" or #choice > 80 then
      return nil, "InvalidChoices"
    end
  end
  if not CrowdPlay_IsEnabled() then
    local code, err = CrowdPlay_RequestSession()
    if not code then return nil, err end
  end
  mSequence = mSequence + 1
  local pollID = "manual-" .. tostring(mSequence) .. "-" .. tostring(math.floor(GetTotalTime() * 1000))
  local path = "/control/start?poll=" .. Escape(pollID)
    .. "&manual=1"
    .. "&duration_ms=" .. tostring(math.floor(duration * 1000))
  for _, choice in ipairs(choices) do
    path = path .. "&choice=" .. Escape(choice)
  end
  local response = Request(path)
  if not response or response[1] ~= "STARTED" then
    return nil, "PollStartFailed"
  end
  mManualPollID = pollID
  mManualPollActive = true
  return pollID, nil
end

function CrowdPlay_CloseManualPoll()
  if not mManualPollActive then
    return false
  end
  local response = Request("/control/end?poll=" .. Escape(mManualPollID))
  if response and response[1] == "ENDED" then
    mManualPollActive = false
    return true
  end
  return false
end

function CrowdPlay_IsManualPollActive()
  return mManualPollActive
end

function CrowdPlay_RefreshPollInfo()
  local response = Request("/control/state")
  if not response or response[1] == "IDLE" then
    mPollSummary = "No vote in progress"
    mPollCountsText = ""
    mManualPollActive = false
    mManualPollID = ""
    return mPollSummary
  end
  if response[1] ~= "ACTIVE" and response[1] ~= "ENDED" then
    mPollSummary = "Vote status unavailable"
    mPollCountsText = ""
    return mPollSummary
  end
  local votes = 0
  local counts = {}
  for count in string.gmatch(response[3] or "", "[^,]+") do
    local value = tonumber(count) or 0
    votes = votes + value
    counts[#counts + 1] = tostring(#counts + 1) .. ":" .. tostring(value)
  end
  mPollCountsText = table.concat(counts, " ")
  local manual = response[2] == mManualPollID and mManualPollID ~= ""
  if not manual then
    mManualPollActive = false
    mManualPollID = ""
  elseif response[1] == "ENDED" then
    mManualPollActive = false
  end
  local kind = manual and "Manual" or "Story"
  if response[1] == "ACTIVE" then
    local seconds = math.ceil((tonumber(response[5]) or 0) / 1000)
    mPollSummary = kind .. " vote: " .. tostring(votes) .. " votes, " .. tostring(seconds) .. "s"
  else
    mPollSummary = kind .. " vote ended: " .. tostring(votes) .. " votes"
  end
  return mPollSummary
end

function CrowdPlay_GetPollSummary()
  return mPollSummary
end

function CrowdPlay_GetPollCountsText()
  return mPollCountsText
end

function CrowdPlay_GetJoinURL(refresh)
  if refresh ~= false and mJoinURL == "" then
    RefreshPlayers()
  end
  return mJoinURL
end

function CrowdPlay_OpenQRCode()
  if not CrowdPlay_IsEnabled() then return false end
  local qrURL, changed = string.gsub(CrowdPlay_GetJoinURL(), "/join/([^/?#]+)$", "/qr/%1", 1)
  if changed ~= 1 then return false end
  local ok, opened = pcall(PlatformOpenURL, qrURL)
  return ok and opened ~= false
end

function CrowdPlay_GetJoinAddress(refresh)
  local url = CrowdPlay_GetJoinURL(refresh)
  if url == "" then
    if mNetworkMode == "ONLINE" and mNetworkStatus == "PENDING" then
      return "Connecting..."
    end
    if mNetworkMode == "ONLINE" then
      return "Tunnel unavailable"
    end
    return "Unavailable"
  end
  if mNetworkMode == "ONLINE" then
    return (string.gsub(url, "/join/.*$", ""))
  end
  url = string.gsub(url, "^https?://", "")
  return (string.gsub(url, "/join/.*$", ""))
end

function CrowdPlay_GetJoinAddressLabel(refresh)
  local address = CrowdPlay_GetJoinAddress(refresh)
  if mNetworkMode == "ONLINE" and #address > 32 and
      string.sub(address, -18) == ".trycloudflare.com" then
    -- The menu's native button is too narrow for Cloudflare's random host.
    -- Keep the HTTPS scheme and tunnel domain visible; clicking copies all of it.
    return string.sub(address, 1, 11) .. "..." .. string.sub(address, -18)
  end
  return address
end

function CrowdPlay_GetMenuStatusText()
  if CrowdPlay_IsEnabled() then
    if not mServerHealthy or not mHostEnabled then
      return "Crowd Play: Local host unavailable"
    end
    return "Crowd Play: On"
  end
  if not RefreshPlayers() then
    return "Crowd Play: Local host unavailable"
  end
  ReadConfig()
  return "Crowd Play: Off"
end

function CrowdPlay_ToggleSession()
  if CrowdPlay_IsEnabled() then
    CrowdPlay_EndSession()
  else
    CrowdPlay_RequestSession()
  end
  if rawget(_G, "CrowdPlayOverlay_Update") then
    CrowdPlayOverlay_Update()
  end
  local menu = Menu_GetCurrentMenu()
  if menu and menu.Refresh then
    menu:Refresh()
  end
end

function CrowdPlay_Noop()
end

function CrowdPlay_SimulateError()
end

function CrowdPlay_RequestDebugging()
end


-- Pause menu scripts keep running while gameplay scripts are suspended.
function CrowdPlay_NotifyPause()
  return ReportPauseState(true)
end
