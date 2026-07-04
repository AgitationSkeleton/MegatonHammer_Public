# Dialogue-NPC gesture catalog (OoT; MM shares the pattern)

Survey of the gestures dialogue-capable NPCs play when talked to, for the Dialogue Editor's per-actor
"Gesture" picker. **Key mechanism finding:** gestures are **actor-internal**, not message-driven. The engine
helper `Npc_UpdateTalking` (z_actor.c) only tracks a 4-value talk *state* (`NPC_TALK_STATE_IDLE/TALKING/
ACTION/ITEM_GIVEN`); each actor's own callback + `actionFunc` state machine picks which animation to play,
usually keyed on the current `actor->textId`, params, or story flags. **Message/textbox data carries no
gesture field.** So a generic "play gesture N" needs a per-actor hook (or our En_MhTalk, if it loads a model)
— it is NOT a vanilla message capability. Each actor's `sAnimationInfo[]` array IS indexable, so "gesture #N"
is a meaningful per-actor selector; the editor exposes it as metadata that a supporting runtime can honour.

## NPCs with distinct conversational gestures (best editor candidates)

| NPC (actor) | Gesture animations (index → description) | Trigger |
|---|---|---|
| **En_Zo** Zora | 6 hands-on-hips foot-tap (impatient); 7 open-arms; 5 throw-rupees; 0/1 idle; 3/4 tread-water | **textId** (0x4006→foot-tap, 0x4007→open-arms) — clearest map |
| **En_Kz** King Zora | 2 "mweep" cry; 0/1 idle | textId 0x4012/0x401F via NPC_TALK_STATE_ACTION |
| **En_Mm/En_Mm2** Running Man | 5 excited, 6 happy (0 run,1 sit,2 sit-wait,3 stand,4 sprint) | Bunny-Hood/marathon textId + choice |
| **En_Sa** Saria | 2 arm-extended, 3 hands-out, 4 hands-on-hips, 5 point, 7 behind-back, 8 hands-on-face | textId in talk callback |
| **En_Md** Mido | 2 raise-hand(halt), 3 halt, 4 hand-down, 5/6 annoyed idle, 11 angry-slam, 12 raise-hand2, 13 angry-head-turn | textId |
| **En_Zl4** Child Zelda | 28 laugh, 29 happy/hands-together, 31 cock-head, 32 happy-clasped, 26/27 point-at-window, 5 surprised, 6 lean-in | cutscene/talk funcs |
| **En_Ta** Talon | arm raise/lower (`gTalonSitHandsUpAnim`, frames 8→29) during cucco game; +stand/sleep/wake/run | cucco-game textId 0x2081–84 + flags |
| **En_Du** Darunia | 7/9/10/13 joy-dance loops, 14 dance-end, 8 transition | cutscene cue |
| **En_Daiku_Kakariko** Carpenter | 4 talk-entry gesture, then 0 idle | on talk entry |
| **En_Ko** Kokiri/Bros/Fado | posture *selected at init* per forest-quest (7 laugh, 11 punch, 12 hand-on-chest, 13 hands-on-hips, 20 backflip, …); Fado plays 7 laugh at textId 0x10B9 | init/forest-quest (held pose) |
| **En_Niw_Lady** Anju | 0 idle, 1 dejected, 2 happy | cucco-quest flags (held) |
| **En_Bom_Bowl_Man** Bowling Lady | 0 idle, 1 reach-out, 2 lean-forward | minigame state |

## NPCs with NO distinct gesture (idle-talk loop + head/torso tracking only)

En_Ma1/2/3 Malon (giggle is **voice SFX**, not skeletal), En_Zl1/2/3 Zelda (cutscene loops), En_Hy townsfolk
(21 variants, all idle-talk; beggar-only idx 23 on item give-up), En_In Ingo (action-func poses, not
per-message), En_Ge2/Ge3 Gerudo, En_Daiku fortress carpenter (CELEBRATE = story), En_Dns Deku salesman,
En_Go rolling Goron, En_Ossan shopkeepers incl. Happy Mask (only idle + look-around), En_Mu/En_Guest/En_Cs/
En_Tk/En_Dog, En_Heishi guards (pointing is cutscene). *(En_Go2's array is unverified — treat with caution.)*

## Editor implication

- The Gesture field is stored per message box (`MhMessage.Gesture`, an index).
- For the listed candidate NPCs, "gesture #N" is meaningful and could be honoured by a small per-actor hook
  that reads the message's gesture id and calls `Animation_Change` on the actor by its `sAnimationInfo[]`
  index. That hook is per-NPC (or a generic patch to `Npc_UpdateTalking`), not a vanilla path.
- Our portable `ovl_En_MhTalk` is a generic talker with no NPC skeleton, so it has no built-in gestures unless
  configured with a model+anim set (a later extension). Reskinning an existing NPC keeps that NPC's own
  gestures (they fire on their own textId logic).

Source: z_actor.c `Npc_UpdateTalking`; per-actor `sAnimationInfo[]` in each `ovl_En_*`.
