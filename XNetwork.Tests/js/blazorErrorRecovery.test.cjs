const test = require('node:test');
const assert = require('node:assert/strict');
const recovery = require('../../XNetwork/wwwroot/js/blazorErrorRecovery.js');

test('first fatal error requests one guarded reload', () => {
    const decision = recovery.decideRecovery(null, 1_000);

    assert.equal(decision.action, 'reload');
    assert.equal(decision.state.attempts, 1);
    assert.equal(decision.state.windowStartedAt, 1_000);
});

test('repeated fatal errors stop reloading and enter cooldown', () => {
    const first = recovery.decideRecovery(null, 1_000);
    const second = recovery.decideRecovery(first.state, 2_000);
    const exhausted = recovery.decideRecovery(second.state, 3_000);
    const duringCooldown = recovery.decideRecovery(exhausted.state, 4_000);

    assert.equal(second.action, 'reload');
    assert.equal(second.state.attempts, 2);
    assert.equal(exhausted.action, 'fallback');
    assert.equal(exhausted.state.cooldownUntil, 3_000 + recovery.options.cooldownMs);
    assert.equal(duringCooldown.action, 'fallback');
});

test('expired cooldown resets the retry budget', () => {
    const decision = recovery.decideRecovery({
        attempts: 2,
        windowStartedAt: 1_000,
        cooldownUntil: 5_000
    }, 5_001);

    assert.equal(decision.action, 'reload');
    assert.equal(decision.state.attempts, 1);
    assert.equal(decision.state.cooldownUntil, 0);
});

test('stable-session cleanup removes the guard state', () => {
    const values = new Map([[recovery.storageKey, '{"attempts":2}']]);
    const storage = {
        getItem: key => values.get(key) ?? null,
        setItem: (key, value) => values.set(key, value),
        removeItem: key => values.delete(key)
    };

    recovery.clearState(storage);

    assert.equal(storage.getItem(recovery.storageKey), null);
});
