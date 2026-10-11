-- Season One has no native Crowd Play HUD. Reuse its persistent UI scene.
-- ui_credits_text.prop uses the same Minecraft font as ui_overlay_text.prop
-- without that property's fullscreen black background.

local kScene = "ui_overlay.scene"
local kAgent = "ui_overlay_crowdPlayLAN"
local mTextAgent = nil

local function PositionText()
  if mTextAgent and AgentExists(kAgent) then
    AgentSetWorldPosFromLogicalScreenPos(mTextAgent, Vector(0.86, 0.07, 0))
  end
end

function CrowdPlayOverlay_Update()
  if not AgentExists(kAgent) then
    mTextAgent = nil
    return
  end
  -- The overlay scene can be recreated while navigating menus. A saved agent
  -- handle may then refer to the old scene even though this name exists again.
  mTextAgent = AgentFind(kAgent)
  if not mTextAgent then
    return
  end
  local visible = CrowdPlay_IsEnabled()
  AgentHide(kAgent, not visible)
  if not visible then
    return
  end

  local code = CrowdPlay_GetSessionCode()
  -- Network callbacks can run while the tunnel is still connecting. Read the
  -- cached link so an overlay repaint cannot start another control request.
  local url = CrowdPlay_GetJoinURL(false)
  local players = CrowdPlay_GetNumServerPlayers()
  local guestLabel = players == 1 and " guest" or " guests"
  local text = "^glyphScale:0.22^" .. url .. "\n"
    .. "^glyphScale:0.34^" .. code .. "  |  " .. tostring(players) .. guestLabel .. "  CP"
  AgentSetProperty(mTextAgent, "Text String", text)
  PositionText()
end

function CrowdPlayOverlay_OnSceneAdd()
  if AgentExists(kAgent) then
    mTextAgent = AgentFind(kAgent)
  else
    local scene = SceneFind(kScene)
    if not scene then
      return
    end
    -- Match UI.lua's runtime AgentCreate form: a loaded Scene object and
    -- the explicit scene-owned flag. Its tool-only asset builder uses a
    -- filename plus an extra persistence flag instead.
    mTextAgent = AgentCreate(kAgent, "ui_credits_text.prop", nil, nil, scene, true)
    if not mTextAgent then
      return
    end
  end
  -- Match the former overlay text's size and color with the transparent prop.
  AgentSetProperty(mTextAgent, "Text Scale", 2.0)
  AgentSetProperty(mTextAgent, "Text Color", Color(1, 1, 1, 0.8))
  Callback_CrowdPlayRefresh:Remove(CrowdPlayOverlay_Update)
  Callback_CrowdPlayRefresh:Add(CrowdPlayOverlay_Update)
  Callback_CrowdPlayNumPlayers:Remove(CrowdPlayOverlay_Update)
  Callback_CrowdPlayNumPlayers:Add(CrowdPlayOverlay_Update)
  Callback_CrowdPlayServerHealth:Remove(CrowdPlayOverlay_Update)
  Callback_CrowdPlayServerHealth:Add(CrowdPlayOverlay_Update)
  CrowdPlayOverlay_Update()
end
