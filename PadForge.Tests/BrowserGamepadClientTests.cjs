const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const sourcePath = path.join(__dirname, '..', 'PadForge.App', 'WebAssets', 'js', 'gamepad_client.js');
function rig(playEffect, phone = true) {
    const timers = new Map();
    let nextTimer = 0, resets = 0, nowMs = 100;
    const pad = { index: 0, id: 'test pad', connected: true, mapping: 'standard', buttons: [], axes: [],
        vibrationActuator: { playEffect, reset() { resets++; return Promise.resolve(); } } };
    const context = {
        document: { hidden: false, addEventListener() {}, getElementById() { return null; } },
        navigator: { getGamepads() { return [pad]; } },
        performance: { now() { return nowMs; } },
        sessionStorage: { getItem() { return null; }, setItem() {} },
        setInterval(fn, ms) { const id = ++nextTimer; timers.set(id, { fn, ms }); return id; },
        clearInterval(id) { timers.delete(id); },
        addEventListener() {}
    };
    if (phone) context.navigator.vibrate = () => true;
    context.window = context;
    const source = fs.readFileSync(sourcePath, 'utf8');
    const anchor = '    document.addEventListener("DOMContentLoaded", start);';
    assert.equal(source.split(anchor).length, 2);
    // Expose the actual client functions to the test host without starting its page.
    vm.runInNewContext(source.replace(anchor,
        'window.testClient = { claimSlot, setRumble, applyRumble, onPing, stopAllRumble };'), context);
    const client = context.testClient;
    const slot = client.claimSlot(pad);
    Object.assign(slot, { live: true, index: 0, lastPingTs: 100 });
    return { client, slot, timers, resetCount: () => resets, setTime: value => { nowMs = value; } };
}
const settled = async () => { await Promise.resolve(); await Promise.resolve(); };

test('a synchronous actuator failure leaves only the phone timer', () => {
    const r = rig(() => { throw new Error('actuator failed'); });
    try {
        r.client.setRumble(r.slot, 65535, 32767);
        assert.equal(r.slot.actuatorFailed, true);
        assert.equal(r.slot.phoneLevel, 1);
        assert.equal(r.slot.rumbleTimer, null);
        assert.deepEqual([...r.timers.values()].map(t => t.ms), [150]);
    } finally { r.client.stopAllRumble(); }
    assert.equal(r.timers.size, 0);
});

test('a synchronous actuator failure without a vibrator leaves no timer', () => {
    const r = rig(() => { throw new Error('actuator failed'); }, false);
    try {
        r.client.setRumble(r.slot, 65535, 32767);
        assert.equal(r.slot.actuatorFailed, true);
        assert.equal(r.slot.rumbleTimer, null);
        assert.equal(r.timers.size, 0);
    } finally { r.client.stopAllRumble(); }
});

test('a working actuator fires immediately and renews until stopped', async () => {
    let effects = 0;
    const r = rig(() => { effects++; return Promise.resolve('complete'); });
    try {
        r.client.setRumble(r.slot, 65535, 32767);
        assert.equal(effects, 1);
        assert.deepEqual([...r.timers.values()].map(t => t.ms), [100]);
        [...r.timers.values()][0].fn();
        assert.equal(effects, 2);
        await settled();
        assert.equal(r.slot.actuatorFailed, false);
        r.client.setRumble(r.slot, 0, 0);
        assert.equal(r.resetCount(), 1);
        assert.equal(r.timers.size, 0);
    } finally { r.client.stopAllRumble(); }
});

test('an asynchronous rejection cancels the actuator timer on fallback', async () => {
    const r = rig(() => Promise.reject(new Error('actuator failed')));
    try {
        r.client.setRumble(r.slot, 65535, 32767);
        assert.deepEqual([...r.timers.values()].map(t => t.ms), [100]);
        await settled();
        assert.equal(r.slot.actuatorFailed, true);
        assert.equal(r.slot.rumbleTimer, null);
        assert.deepEqual([...r.timers.values()].map(t => t.ms), [150]);
    } finally { r.client.stopAllRumble(); }
});

test('a stale rejection cannot cancel a newer actuator request', async () => {
    let rejectOld, calls = 0;
    const old = new Promise((_, reject) => { rejectOld = reject; });
    const r = rig(() => ++calls === 1 ? old : Promise.resolve('complete'));
    try {
        r.client.setRumble(r.slot, 65535, 32767);
        const previous = r.slot.rumbleTimer;
        r.client.setRumble(r.slot, 32767, 0);
        const current = r.slot.rumbleTimer;
        assert.notEqual(current, previous);
        assert.equal(r.timers.has(previous), false);
        rejectOld(new Error('old request failed'));
        await settled();
        assert.equal(r.slot.actuatorFailed, false);
        assert.equal(r.slot.rumbleTimer, current);
        assert.deepEqual([...r.timers.values()].map(t => t.ms), [100]);
    } finally { r.client.stopAllRumble(); }
});


test('a neutral ping cannot revive an expired positive request', async () => {
    let effects = 0;
    const r = rig(() => { effects++; return Promise.resolve('complete'); });
    try {
        r.setTime(3201);
        r.client.applyRumble(r.slot, 65535, 32767);
        assert.equal(effects, 0);
        assert.equal(r.slot.rumbleLeft, 0);
        r.setTime(3250);
        r.client.onPing(r.slot, { l: 0, r: 0, n: 1 });
        for (const timer of [...r.timers.values()]) timer.fn();
        await settled();
        assert.equal(effects, 0);
        assert.equal(r.slot.rumbleLeft, 0);
        assert.equal(r.timers.size, 0);
    } finally { r.client.stopAllRumble(); }
});
