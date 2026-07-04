/*
 * ovl_En_MhTalk — Megaton Hammer portable "dialogue point" actor (MIT).
 *
 * A generic, invisible talk trigger: place it (params = entry textId), and when Link talks to it, it
 * runs the authored dialogue for that textId from gMhDialogueTable (mh_dialogue.h) — presenting the
 * message and applying the outcome (branch / give item / charge rupees / set flag), with an
 * "already fulfilled" fallback message. It is deliberately fork-independent: it uses only stock
 * decomp API and the portable table. Reskinning an EXISTING NPC instead keeps that NPC's own gestures;
 * this actor has no model of its own (attach it beside a sign/NPC, or extend it with one).
 *
 * INTEGRATION (see README.md): give it a free ACTOR_ id + overlay-table row, add this .c + the
 * generated mh_dialogue_data.c to your build. API names below are the mainline zeldaret names; a few
 * differ slightly on some bases (noted inline) — adapt if your tree renames them.
 *
 * This is REFERENCE SOURCE: written against the decomp API, not compiled by the editor. Verify against
 * your base's headers before shipping.
 */
#include "z64.h"
#include "regs.h"
#include "mh_dialogue.h"

#define FLAGS ACTOR_FLAG_4 /* keep active while off-screen so state persists mid-conversation */

typedef struct EnMhTalk {
    /* 0x0000 */ Actor actor;
    /* ...    */ ColliderCylinder collider;
    /* ...    */ const MhDialogueEntry* entry; /* row for the currently-shown textId */
    /* ...    */ u16 activeTextId;
    /* ...    */ u8  waitingResult;            /* 1 = a message is open; apply its outcome on close */
} EnMhTalk;

static ColliderCylinderInit sCylinderInit = {
    { COLTYPE_NONE, AT_NONE, AC_NONE, OC1_ON | OC1_TYPE_ALL, OC2_TYPE_2, COLSHAPE_CYLINDER },
    { ELEMTYPE_UNK0, { 0x00000000, 0x00, 0x00 }, { 0x00000000, 0x00, 0x00 }, TOUCH_NONE, BUMP_NONE, OCELEM_ON },
    { 20, 50, 0, { 0, 0, 0 } },
};

const MhDialogueEntry* MhDialogue_Find(u16 textId) {
    s32 i;
    for (i = 0; i < gMhDialogueCount; i++) {
        if (gMhDialogueTable[i].textId == textId) {
            return &gMhDialogueTable[i];
        }
    }
    return NULL;
}

/* The textId this point currently offers: the fulfilled fallback if its done flag is set, else the entry. */
static u16 EnMhTalk_CurrentTextId(EnMhTalk* this, PlayState* play) {
    const MhDialogueEntry* e = MhDialogue_Find(this->actor.params & 0xFFFF);
    if (e != NULL && e->doneFlag >= 0 && e->afterMsgId >= 0 && Flags_GetSwitch(play, e->doneFlag)) {
        return (u16)e->afterMsgId;
    }
    return (u16)(this->actor.params & 0xFFFF);
}

/* Apply one outcome on accept (choice confirmed / message advanced). Returns 1 if it opened a new box. */
static s32 EnMhTalk_ApplyOutcome(EnMhTalk* this, PlayState* play, const MhOutcome* o) {
    if (o->rupeeCost > 0) {
        if (gSaveContext.rupees < o->rupeeCost) {
            return 0; /* can't afford — leave the transaction unfulfilled (author a "not enough" branch if wanted) */
        }
        Rupees_ChangeBy(-(s32)o->rupeeCost);
    }
    if (o->giveItem >= 0) {
        /* Offer the item the standard way; picked up next frame. (Some bases: func_8002F434.) */
        Actor_OfferGetItem(&this->actor, play, o->giveItem, this->actor.xzDistToPlayer, this->actor.playerHeightRel);
    }
    if (o->fireFlag >= 0) {
        Flags_SetSwitch(play, o->fireFlag);
    }
    if (o->nextMsgId >= 0) {
        Message_ContinueTextbox(play, o->nextMsgId);
        return 1;
    }
    return 0;
}

void EnMhTalk_Init(Actor* thisx, PlayState* play) {
    EnMhTalk* this = (EnMhTalk*)thisx;
    Collider_InitCylinder(play, &this->collider);
    Collider_SetCylinder(play, &this->collider, &this->actor, &sCylinderInit);
    Actor_SetScale(&this->actor, 0.01f);
    this->actor.targetMode = 6; /* talk range */
    this->waitingResult = 0;
}

void EnMhTalk_Destroy(Actor* thisx, PlayState* play) {
    EnMhTalk* this = (EnMhTalk*)thisx;
    Collider_DestroyCylinder(play, &this->collider);
}

void EnMhTalk_Update(Actor* thisx, PlayState* play) {
    EnMhTalk* this = (EnMhTalk*)thisx;

    if (this->waitingResult) {
        /* A message is open: wait for the player to choose / dismiss, then apply the outcome. */
        if (Message_GetState(&play->msgCtx) == TEXT_STATE_CHOICE && Message_ShouldAdvance(play)) {
            const MhOutcome* o = &this->entry->outcome[play->msgCtx.choiceIndex & 1];
            if (!EnMhTalk_ApplyOutcome(this, play, o)) {
                Message_CloseTextbox(play);
                this->waitingResult = 0;
            }
        } else if (Message_GetState(&play->msgCtx) == TEXT_STATE_CLOSING) {
            if (this->entry != NULL && !this->entry->isPrompt) {
                EnMhTalk_ApplyOutcome(this, play, &this->entry->outcome[0]); /* plain message: advance outcome */
            }
            this->waitingResult = 0;
        }
        return;
    }

    this->collider.dim.pos.x = (s16)this->actor.world.pos.x;
    this->collider.dim.pos.y = (s16)this->actor.world.pos.y;
    this->collider.dim.pos.z = (s16)this->actor.world.pos.z;
    CollisionCheck_SetOC(play, &play->colChkCtx, &this->collider.base);

    if (Actor_TalkOfferAccepted(&this->actor, play)) {
        this->activeTextId = EnMhTalk_CurrentTextId(this, play);
        this->entry = MhDialogue_Find(this->actor.params & 0xFFFF);
        Message_StartTextbox(play, this->activeTextId, &this->actor);
        this->waitingResult = 1;
    } else {
        /* Offer to talk when Link is near and facing. */
        Actor_OfferTalk(&this->actor, play, 100.0f);
    }
}

/* No draw: this is an invisible talk point. Attach beside a sign/NPC, or extend with a model. */
void EnMhTalk_Draw(Actor* thisx, PlayState* play) {
}

ActorInit En_MhTalk_InitVars = {
    /* ACTOR_EN_MHTALK */ 0,
    ACTORCAT_NPC,
    FLAGS,
    OBJECT_GAMEPLAY_KEEP,
    sizeof(EnMhTalk),
    (ActorFunc)EnMhTalk_Init,
    (ActorFunc)EnMhTalk_Destroy,
    (ActorFunc)EnMhTalk_Update,
    (ActorFunc)EnMhTalk_Draw,
};
