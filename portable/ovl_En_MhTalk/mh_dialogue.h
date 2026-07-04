/*
 * mh_dialogue.h — Megaton Hammer portable dialogue outcome table (MIT).
 *
 * Presentation (text, colour, timing, SFX, two-choice prompt, goto-message) lives in the NORMAL game
 * message data and works on any OoT base with no code. This table carries only the BEHAVIOUR the message
 * format can't express — branch / give item / charge rupees / set flag / "already fulfilled" fallback —
 * keyed by the message's textId. Megaton Hammer generates mh_dialogue_data.c (the gMhDialogueTable rows);
 * you compile it + ovl_En_MhTalk into your project (vanilla decomp, SoH, 2Ship, HackerOoT, a TC mod, …).
 * Nothing here is specific to any fork.
 */
#ifndef MH_DIALOGUE_H
#define MH_DIALOGUE_H

#include "ultra64.h"

typedef struct {
    /* -1 = none for every s16 field below */
    s16 nextMsgId;   /* branch: Message_ContinueTextbox(nextMsgId) */
    s16 fireFlag;    /* Flags_SetSwitch(play, fireFlag) — usually the "done" flag */
    s16 giveItem;    /* a GI_* getItemId offered on accept */
    u16 rupeeCost;   /* 0 = free; else Rupees_ChangeBy(-cost), gated on affording it */
} MhOutcome;

typedef struct {
    u16 textId;        /* the message this row describes */
    u16 sfx;           /* 0 = none (usually also emitted inline by the message's CTRL_SFX) */
    s8  gesture;       /* -1 = default; else an index into a supporting NPC's sAnimationInfo[] */
    u8  isPrompt;      /* 0 = plain message (uses outcome[0] on advance); 1 = two-choice */
    MhOutcome outcome[2]; /* [0] = advance / choice "Yes"; [1] = choice "No" */
    s16 doneFlag;      /* -1 = none; when Flags_GetSwitch(doneFlag) is set, show afterMsgId instead */
    s16 afterMsgId;    /* -1 = none */
} MhDialogueEntry;

extern const MhDialogueEntry gMhDialogueTable[];
extern const s32 gMhDialogueCount;

/* Linear lookup by textId (tables are small); returns NULL if not authored. */
const MhDialogueEntry* MhDialogue_Find(u16 textId);

#endif
