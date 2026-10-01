-- DaProdMod: modifiche all'interfaccia di Ponticheage (si installa da solo con il launcher).
-- Collegamento con il server: una "posta" a DaProd (il server la intercetta, non viene consegnata nulla).

DAPROD = DAPROD or {}

local function say(msg)
    pcall(function() AddMessageToSysMsgWindow(msg) end)
end
DAPROD.Say = say

-- manda un comando al server (titolo = comando, testo = argomenti)
function DAPROD.Send(cmd, text)
    local ok, err = pcall(function()
        X2Mail:SendMail({
            receiver = "DaProd", title = cmd, text = text or "", money = 0,
            doodadId = 0, type = MAIL_EXPRESS, withReceiver = "0"
        })
    end)
    if not ok then say("[DaProd] invio non riuscito: " .. tostring(err)) end
end

---------------------------------------------------------------------------------------------
-- Replace Effect: dopo la sostituzione compare una finestra con "Tieni il nuovo" / "Ripristina il vecchio"
---------------------------------------------------------------------------------------------
local choice = nil

local function BuildChoice()
    local frame = CreateWindow("daprodRerollChoice", "UIParent")
    frame:Show(false)
    frame:SetExtent(440, 210)
    frame:AddAnchor("CENTER", 0, 170)
    frame:SetTitle("Effetto sostituito")
    frame:SetCloseOnEscape(true)
    CreateWindowDefaultTextButtonSet(frame, { leftButtonStr = "Tieni il nuovo", rightButtonStr = "Ripristina il vecchio" })

    frame.lines = {}
    for i = 1, 3 do
        local label = CreateDefaultStyleLabel("daprodRerollLine" .. i, frame)
        label:SetExtent(400, 24)
        label:AddAnchor("TOPLEFT", frame, 20, 52 + (i - 1) * 32)
        label.style:SetAlign(ALIGN_LEFT)
        label:Show(true)
        frame.lines[i] = label
    end

    ButtonOnClickHandler(frame.leftButton, function() frame:Show(false) end)
    ButtonOnClickHandler(frame.rightButton, function()
        DAPROD.Send("undo")
        frame:Show(false)
    end)
    return frame
end

local function ShowChoice(before, after)
    if choice == nil then choice = BuildChoice() end
    choice.lines[1]:SetText("Prima:  " .. (before or "?"))
    choice.lines[2]:SetText("Adesso:  " .. (after or "?"))
    choice.lines[3]:SetText("Vuoi tenere il nuovo effetto o rimettere il vecchio?")
    choice:Show(true)
    choice:Raise()
end

local function AttrStr(name, value)
    local ok, text = pcall(function()
        return string.format("%s %s", locale.attribute(name), GetModifierCalcValue(name, value))
    end)
    return ok and text or tostring(name)
end

-- stessa finestra di risultato del gioco, in piu' la scelta
local function ShowReRollResult(dialog, infoTable)
    pcall(function()
        dialog:SetTitle(GetCommonText("change_evolving_effect"))
        dialog:UseExpandWidth()

        dialog:RegisterStack(W_MODULE:Create("textbox", dialog, W_MODULE.TYPES.TEXTBOX, {
            [W_MODULE.ATTRIBUTE.TEXT] = GetUIText(MSG_BOX_BODY_TEXT, "re_roll_evolving_result_body")
        }))
        dialog:RegisterStack(W_MODULE:Create("itemIcon", dialog, W_MODULE.TYPES.ICON_VERTICAL, {
            itemInfo = infoTable["itemInfo"], stack = 1,
        }))

        local headerTable = W_MODULE:Create("changeTable", dialog, W_MODULE.TYPES.HEADER_TABLE_A, {
            [W_MODULE.ATTRIBUTE.LEFT_WIDTH_PRESET] = "middle",
        })
        dialog:RegisterStack(headerTable)
        headerTable:AddRow("before", {
            ["title"] = { ["string"] = GetCommonText("evolving_dialog_before_attr_title") },
            ["value"] = { ["string"] = AttrStr(infoTable.before.name, infoTable.before.value) },
        })
        headerTable:AddRow("after", {
            ["title"] = { ["string"] = GetCommonText("evolving_dialog_after_attr_title") },
            ["value"] = { ["string"] = AttrStr(infoTable.after.name, infoTable.after.value), ["colorKey"] = "blue" },
        })
    end)

    pcall(function()
        ShowChoice(AttrStr(infoTable.before.name, infoTable.before.value), AttrStr(infoTable.after.name, infoTable.after.value))
    end)
end

local function Install()
    local ok, err = pcall(function()
        X2DialogManager:SetHandler(DLG_TASK_RE_ROLL_EVOLVING_RESULT_NOTICE, ShowReRollResult)
    end)
    if not ok then say("[DaProd] mod: " .. tostring(err)) end
end

Install()

-- le schermate del gioco possono registrare i loro gestori dopo l'addon: lo rifaccio ad ogni caricamento
local okw, errw = pcall(function()
    local watcher = CreateEmptyWindow("daprodWatcher", "UIParent")
    watcher:Show(true)
    local announced = false
    local events = {
        ["LEFT_LOADING"] = function()
            Install()
            if not announced then
                announced = true
                say("[DaProd] mod dell'interfaccia attivo.")
            end
        end,
    }
    watcher:SetHandler("OnEvent", function(this, event, ...) if events[event] then events[event](...) end end)
    RegistUIEvent(watcher, events)
end)
if not okw then say("[DaProd] mod: " .. tostring(errw)) end
