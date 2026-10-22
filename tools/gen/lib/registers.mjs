// Keypad register map of the result payload (docs/PROTOCOL.md 2.2, 10.3, 13.5, 14.2). Registers are player variables
// ck3xbl_r0 .. ck3xbl_r199. Shared by the mod generator, the reference payload encoder and the self-test.

export const REG = {
  count: 200,
  checksum: 199,
  modulus: 9973,
  // r0. Version 3 uses the position-weighted checksum; version 2 (plain sum) is still accepted by the mod. Version 4 (the
  // battle clock, PROTOCOL 14) keeps the version 3 checksum and adds the kill targets, the steering hint and absolute levy
  // survivors. The reference encoder defaults to version 3; the mod accepts every version up to maxPayloadVersion and logs
  // that number as ck3xbl_in_maxver in every IN dump.
  payloadVersion: 3,
  legacyPayloadVersion: 2,
  steerPayloadVersion: 4,
  maxPayloadVersion: 4,
  // Version 3: register i is weighted (i mod weightCycle) + 1.
  weightCycle: 16,
  version: 0,
  bid: 1,
  outcome: 2,
  province: 3,
  summary: { myLost: 4, enemyLost: 5, myKills: 6, prestige: 7 },
  // Payload 4 only (0 in payload 3): the enemy dead each side's CK3 combat must count (cumulative over clashes), and the
  // steering hint r198 = pace day + 100 x flags (PROTOCOL 14.2).
  killTarget: { attacker: 8, defender: 9 },
  steerHint: 198,
  army: { base: 10, stride: 14, slots: 8, maaPairs: 6 },
  character: { base: 130, stride: 3, slots: 20 },
  // Award slots (PROTOCOL 13.5): slot k = (character tag, award value A = R + 10 G + 1000 P) in two blocks of registers that
  // payload 3 left free, filled in order k = 0, 1, ...
  award: { blocks: [{ base: 122, slots: 4 }, { base: 190, slots: 4 }], stride: 2 },
};

/** Payload versions the mod's commit accepts, newest first. */
export const ACCEPTED_VERSIONS = [REG.steerPayloadVersion, REG.payloadVersion, REG.legacyPayloadVersion];

/** Number of award slots over both blocks. */
export const AWARD_SLOTS = REG.award.blocks.reduce((n, b) => n + b.slots, 0);

/** Registers (tag, award value) of award slot `k` (0-based over both blocks). */
export function awardSlotRegisters(k) {
  let index = k;
  for (const block of REG.award.blocks) {
    if (index < block.slots) {
      const tag = block.base + REG.award.stride * index;
      return { tag, award: tag + 1 };
    }
    index -= block.slots;
  }
  throw new Error(`award slot ${k} outside 0..${AWARD_SLOTS - 1}`);
}

// Award value A = R + 10 G + 1000 P: R = prowess rolls (0..3), G = glory / 5, P = prestige / 25 (PROTOCOL 13.5).
export const AWARD = { gloryFactor: 10, prestigeFactor: 1000, maxRolls: 3, gloryUnit: 5, prestigeUnit: 25 };

/** Weight of register `i` in the version 3 checksum. */
export function registerWeight(i) {
  return (i % REG.weightCycle) + 1;
}

/**
 * Checksum r199 over r0..r198 for the given payload version, computed exactly as the mod's ck3xbl_commit_effect does
 * (reduced after every term, so CK3's fixed-point values never exceed about 170,000):
 *   versions 3 and 4: sum of (r_i mod 9973) * ((i mod 16) + 1), mod 9973
 *   version 2: sum of r_i, mod 9973
 */
export function payloadChecksum(registers, version = REG.payloadVersion) {
  let sum = 0;
  for (let i = 0; i < REG.checksum; i++) {
    const term = version === REG.legacyPayloadVersion ? registers[i] : (registers[i] % REG.modulus) * registerWeight(i);
    sum = (sum + term) % REG.modulus;
  }
  return sum;
}
