/**
 * The worked example the UI runs on in a plain browser (`npm run dev`), where there is
 * no host, no League client and no Claude. Inside the app it is never shown: the draft
 * arrives from the client and the recommendations from Claude.
 *
 * Blue side, top lane, pick B3 - the last pick of the first rotation, which is the
 * counterpick seat. Darius and Nidalee are down for red; their third pick is still
 * hidden.
 */

import type { Champion, DraftState, Recommendation } from "../types";

export const CHAMPIONS: Record<string, Champion> = {
  Gwen: { key: "Gwen", name: "Gwen", klass: "Skirmisher", splashPos: "50% 33%" },
  Jax: { key: "Jax", name: "Jax", klass: "Skirmisher", splashPos: "52% 32%" },
  Ornn: { key: "Ornn", name: "Ornn", klass: "Vanguard", splashPos: "50% 38%" },
  Camille: { key: "Camille", name: "Camille", klass: "Diver", splashPos: "50% 28%" },
  Malphite: { key: "Malphite", name: "Malphite", klass: "Vanguard", splashPos: "46% 40%" },
  Aatrox: { key: "Aatrox", name: "Aatrox", klass: "Diver", splashPos: "50% 32%" },
  Darius: { key: "Darius", name: "Darius", klass: "Juggernaut", splashPos: "46% 26%" },
  Nidalee: { key: "Nidalee", name: "Nidalee", klass: "Assassin", splashPos: "50% 25%" },
  Sejuani: { key: "Sejuani", name: "Sejuani", klass: "Vanguard", splashPos: "50% 25%" },
  Orianna: { key: "Orianna", name: "Orianna", klass: "Battlemage", splashPos: "50% 25%" },
};


export const INITIAL_DRAFT: DraftState = {
  ally: [
    { championKey: null, role: "Top", isYou: true },
    { championKey: "Sejuani", role: "Jungle" },
    { championKey: "Orianna", role: "Mid" },
    { championKey: null, role: "Bot" },
    { championKey: null, role: "Support" },
  ],
  enemy: [
    { championKey: "Darius", role: "Top" },
    { championKey: "Nidalee", role: "Jungle" },
    { championKey: null, role: "Mid", onTheClock: true },
    { championKey: null, role: "Bot" },
    { championKey: null, role: "Support" },
  ],
  enemyRoles: { Darius: "Top", Nidalee: "Jungle" },
  bans: [],
  enemyPicksRemaining: 3,
};

/** Ranked best-first. The real list comes back from Claude, scored against this draft. */
export const RECOMMENDATIONS: Recommendation[] = [
  {
    championKey: "Gwen",
    score: 92,
    verdict: "Favorable",
    why: "Outranges his whole kit and gives this comp the damage it's missing.",
    hints: [
      "Q outranges everything he has",
      "W is a real out against a Nidalee gank",
      "Only pool champ that adds damage, not another body",
    ],
    brief: {
      headline:
        "She outranges him, ignores his all-in, and your team needs a damage threat far more than another frontline body. This is the pick unless their last slot turns out to be a hard-engage support.",
      lane: [
        { mark: "Lv 1–2", text: "Concede these two levels. His <strong>Q heals per champion hit on the outer blade</strong> — stand fully inside him or fully out, never on the edge." },
        { mark: "Lv 3", text: "Q max. You clear from outside his range: take the free push and hold prio for Sejuani's first path top." },
        { mark: "Lv 6", text: "Bait <strong>E, Apprehend — 24s at rank 1</strong>. Once it's down you own the next twenty seconds. That's your window to walk up and shove." },
        { mark: "Bleed", text: "Count his stacks out loud. <strong>At 4, disengage for five seconds.</strong> Never trade at 4." },
        { mark: "1st back", text: "Nashor's Tooth is the spike. Before it you win short trades; after it you win the duel outright." },
      ],
      youKill: [
        "Any time his E is down and he's under ~60%",
        "Inside your W with Sejuani pathing top",
        "Post-Nashor's, level 11+, in an extended trade",
      ],
      theyKill: [
        "Levels 1–2, if he lands the blade on you twice",
        "At 4 bleed stacks with Flash up — R executes through W",
        "Anywhere near river with Nidalee unaccounted for",
      ],
      jungle:
        "Nidalee's first clear ends around <strong>3:15</strong>. If she hasn't shown on the map by 3:30, she's coming top and Darius will hold E for it. Ward tri at 3:00 and play the 2v2 rather than backing off — Sejuani beats her in a straight fight.",
      comp:
        "Sejuani plus Orianna is a wombo comp. <strong>You are the damage that converts their engage, not the engage.</strong> Flank wide, hold W until after Sej's R connects, then Q-melt the clump. Do not go split-push after 20 minutes — this team cannot fight 4v5.",
      setup: { keystone: "Conqueror", secondary: "Bone Plating · Second Wind", summoners: "Flash + Teleport", first: "Nashor's Tooth" },
      setupNote: "Ignite beats his sustain in lane, but Teleport is worth more into a wombo comp than one solo kill.",
    },
  },
  {
    championKey: "Jax",
    score: 84,
    verdict: "Favorable",
    why: "Beats him after 6 and gives Sejuani a diver — if you survive levels 1 to 5.",
    hints: [
      "E eats his Q and his autos",
      "Matchup flips completely at level 6",
      "Best 2v2 in your pool into Nidalee",
    ],
    brief: {
      headline:
        "The matchup flips completely at 6. The question isn't whether Jax wins — it's whether you get there without handing two kills to a Nidalee gank.",
      lane: [
        { mark: "Lv 1–2", text: "The worst two levels of the matchup. Take the CS you can reach with a step and nothing else." },
        { mark: "Lv 3", text: "<strong>E eats his Q and his autos.</strong> Bait the Q, E through it, W-auto out. Don't hold E for the pull — the pull isn't what kills you." },
        { mark: "Lv 6", text: "R flips it. Your full combo beats his at even items from here on." },
        { mark: "Bleed", text: "Same rule: 4 stacks means walk. His R executes through your R's armor." },
        { mark: "1st back", text: "Trinity is where you take over the side lane — but only with a ward in river." },
      ],
      youKill: [
        "Level 6 with E up and him under 70%",
        "Any time he Qs the wave instead of you",
        "Post-Trinity, side lane, vision down",
      ],
      theyKill: [
        "Levels 1–5, always",
        "If you E early and he waits it out — E is 16s, his Q is 9s",
        "Under his tower at any point before 11",
      ],
      jungle:
        "You are the best 2v2 champion in your pool into Nidalee once E is up — she has no hard CC to stop the leap. Ping Sejuani top at 3:00, and if Nidalee shows bot instead, invade her raptors rather than shoving.",
      comp:
        "You're the follow-up dive: Sejuani engages, you leap the backline. Jax is also the only champion in this pool who can genuinely side lane in this comp — take it after the first tower falls, never before.",
      setup: { keystone: "Conqueror", secondary: "Bone Plating · Second Wind", summoners: "Flash + Teleport", first: "Trinity Force" },
      setupNote: "Grasp is the safer blind pick. Conqueror is right here because you're the counterpick and you know you're getting to 6.",
    },
  },
  {
    championKey: "Ornn",
    score: 79,
    verdict: "Even",
    why: "Safest pick on the board and the least useful one — Sejuani already does this job.",
    hints: [
      "Nothing goes wrong; nothing goes right",
      "Second frontline in a comp that has one",
      "Nidalee farms free for ten minutes",
    ],
    brief: {
      headline:
        "Nothing goes wrong in this lane and nothing goes right either. The real cost is that Nidalee farms untouched for ten minutes and your comp gets a second frontline it doesn't need.",
      lane: [
        { mark: "Lv 1–2", text: "Nothing happens. Take the CS; W to break his Q when he commits." },
        { mark: "Lv 3", text: "You lose prio and that's acceptable. Farm to 6." },
        { mark: "Lv 6", text: "R is <strong>not a lane spell</strong> into Darius. Save it for the first jungle skirmish." },
        { mark: "1st back", text: "Your item upgrades are the entire reason to take this pick — an upgraded Ludens on Orianna at 13 is a real trade." },
      ],
      youKill: ["Never, alone", "With Sejuani, post-6, into a used E"],
      theyKill: ["Any extended trade before your first back", "Level 6+ with Ghost — he outruns Brittle"],
      jungle:
        "Nidalee farms untouched if you take this. You can't punish her and you can't help Sejuani contest top side. That's the price of the safe pick, and it's higher than it looks.",
      comp:
        "<strong>Redundant.</strong> Sejuani is already your frontline and your engage; a second one means nobody converts the fight. Take this only if their last pick is a hypercarry you need locked down twice.",
      setup: { keystone: "Grasp of the Undying", secondary: "Bone Plating · Overgrowth", summoners: "Flash + Teleport", first: "Heartsteel" },
      setupNote: "Upgrading Sejuani's and Orianna's items at 13 is worth more than anything you'll do in this lane.",
    },
  },
  {
    championKey: "Camille",
    score: 71,
    verdict: "Even",
    why: "Wins early, loses mid-game, best follow-up to Orianna's ult in your pool.",
    hints: [
      "Wins levels 1–3, loses after 6",
      "R is the ideal shockwave follow-up",
      "Side laner in a comp that fights as five",
    ],
    brief: {
      headline:
        "A real conflict: your ultimate is the perfect answer to a shockwave, but Camille wants to be in a side lane and this comp has to fight five-on-five.",
      lane: [
        { mark: "Lv 1–2", text: "You actually win level 1 — Q-auto-Q beats his Q, as long as you're not standing on the blade." },
        { mark: "Lv 3", text: "E over the wall, take the trade, W for sustain, walk out. <strong>Never stay for a fourth auto.</strong>" },
        { mark: "Lv 6", text: "He wins the all-in from here unless you hold E and Flash. His R resets on kill; yours doesn't." },
        { mark: "1st back", text: "Sheen is your spike, but he outscales you straight through the bruiser item timings." },
      ],
      youKill: ["Levels 1–3 with a wall to E off", "Any time his E is down and you have Ignite"],
      theyKill: ["Level 6 onward in a straight all-in", "If you E in without an exit — no disengage once W is spent"],
      jungle:
        "Camille has the best answer to a Nidalee gank in the pool: hookshot out over the wall. Play further up the lane than instinct says.",
      comp:
        "Your R locks one target in a box, which is the ideal follow-up to Orianna's ball. But you're a side laner in a comp built to fight as five, and that tension doesn't resolve itself.",
      setup: { keystone: "Conqueror", secondary: "Magical Footwear · Cosmic Insight", summoners: "Flash + Ignite", first: "Trinity Force" },
      setupNote: "Ignite here, not Teleport — the entire case for Camille is winning the lane before eight minutes.",
    },
  },
  {
    championKey: "Malphite",
    score: 66,
    verdict: "Difficult",
    why: "Great into AD, bad into this AD — and it's a third engage ultimate.",
    hints: [
      "He's AD but not an auto-attacker",
      "Third engage ult in this comp",
      "Worth holding until their last pick",
    ],
    brief: {
      headline:
        "Hold this one until their last pick. Against a diver comp Malphite jumps two tiers; against what you can see on the board right now, it's the wrong shape twice over.",
      lane: [
        { mark: "Lv 1–2", text: "He's AD but he isn't an auto-attacker. <strong>Your passive shield does very little against Q.</strong>" },
        { mark: "Lv 3", text: "Q the wave, back off. You are farming, not laning." },
        { mark: "Lv 6", text: "R is not for this lane. Spend it here and you have nothing for the next skirmish." },
      ],
      youKill: ["With Sejuani, immediately after his E", "Never solo"],
      theyKill: ["Any point you let him reach 5 stacks", "Level 6+ with Ghost, from full HP"],
      jungle: "You can't contest anything top side. Nidalee gets free reign and Sejuani has no partner up here.",
      comp:
        "Third engage tool. Sejuani R, Orianna R, Malphite R — one of them is wasted every fight. This only makes sense if their last pick is a hyper-mobile carry you need pinned.",
      setup: { keystone: "Grasp of the Undying", secondary: "Bone Plating · Second Wind", summoners: "Flash + Teleport", first: "Sunfire Aegis" },
      setupNote: "Genuinely worth holding for their last pick. If they take Yone or Kai'Sa, re-score this one.",
    },
  },
  {
    championKey: "Aatrox",
    score: 58,
    verdict: "Losing",
    why: "Loses the lane at every stage, and there are five better options on the board.",
    hints: [
      "His Q heals more than your passive",
      "His R resets, yours doesn't",
      "Comp fit is fine — the lane isn't",
    ],
    brief: {
      headline:
        "The comp fit is fine: a diver behind Sejuani is exactly right. It's the lane that makes this wrong, and you're the counterpick, so you don't have to take a losing one.",
      lane: [
        { mark: "Lv 1–2", text: "Q3 poke is your only tool and it isn't enough." },
        { mark: "Lv 3", text: "Do not take an extended trade. <strong>His Q heals more than your passive.</strong>" },
        { mark: "Lv 6", text: "His R resets and yours doesn't. He wins the all-in through your revive." },
        { mark: "1st back", text: "If you're losing anyway: Doran's Shield, Second Wind, play for minute fourteen." },
      ],
      youKill: ["Only with a Sejuani gank and Ignite"],
      theyKill: ["Any full trade, at any level", "Especially once he finishes Stridebreaker"],
      jungle: "Nidalee will target this lane specifically, because she can see you have no escape.",
      comp: "Fine on paper. The comp isn't the problem here.",
      setup: { keystone: "Conqueror", secondary: "Triumph · Legend: Alacrity", summoners: "Flash + Teleport", first: "Eclipse" },
      setupNote: "You're the last pick of the rotation. Taking a losing lane from this seat is a choice, not a constraint.",
    },
  },
];

/**
 * The pool-blind half of the same answer: the best top laners on the board for this
 * draft, whether or not they are played. Inside the app these come back from the same
 * Claude call as the shortlist, scored on the same scale - here the gap is four points,
 * which is the case the section is for: the pool is fine, and you can see that it is.
 */
export const OPEN_PICKS: Recommendation[] = [
  {
    championKey: "Kennen",
    score: 96,
    verdict: "Favorable",
    why: "Ranged into a juggernaut, and a third AoE ult that converts Sejuani's engage instead of repeating it.",
    hints: [
      "Darius cannot reach him before 6",
      "R after Sejuani R is the whole fight",
    ],
  },
  {
    championKey: "Gwen",
    score: 92,
    verdict: "Favorable",
    why: "Your own pick is the second best on the board — nothing outside your pool beats it by much.",
    hints: [
      "Already in your pool, already ranked first",
      "Only four points off the open best",
    ],
  },
  {
    championKey: "Jayce",
    score: 88,
    verdict: "Favorable",
    why: "Wins the lane outright and gives the comp poke, but he falls off in the 5v5 this team wants.",
    hints: [
      "Beats Darius at every level",
      "Weakest of the three once fights start",
    ],
  },
];

/** Seeded notes so the notes feature is visible before any game has been played. */
export const SEED_NOTES: Record<string, { id: number; body: string; createdAt: string }[]> = {
  "Gwen|Darius|Top": [
    { id: 2, body: "I keep auto-ing him at 4 stacks and dying to the R reset. Count out loud, out of the trade.", createdAt: "2026-08-28" },
    { id: 1, body: "W does nothing if he's standing inside it with me. Place it to break his vision, then walk out of it.", createdAt: "2026-07-14" },
  ],
  "Jax|Darius|Top": [
    { id: 3, body: "Stop E-ing on reaction. Walk at him, let him throw Q first, then E. Won three of four doing it that way.", createdAt: "2026-08-02" },
  ],
  "Aatrox|Darius|Top": [
    { id: 4, body: "Third time I've blind-picked this into a bruiser and regretted it. Stop.", createdAt: "2026-06-19" },
  ],
};

export const SEED_RECORDS: Record<string, { wins: number; losses: number }> = {
  Gwen: { wins: 7, losses: 3 },
  Jax: { wins: 11, losses: 6 },
  Ornn: { wins: 5, losses: 4 },
  Camille: { wins: 4, losses: 5 },
  Malphite: { wins: 2, losses: 3 },
  Aatrox: { wins: 3, losses: 8 },
};
