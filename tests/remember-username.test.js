const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const script = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "js", "site.js"), "utf8");
const key = "smartclassac.rememberedUsername";

function setup(initialValue = null, failure = "") {
    const values = new Map(initialValue === null ? [] : [[key, initialValue]]);
    const handlers = {};
    const username = { value: "" };
    const remember = {
        checked: false,
        disabled: true,
        addEventListener(name, handler) { handlers[name] = handler; }
    };
    const error = { textContent: "" };
    const form = {
        elements: { username },
        querySelector(selector) { return selector === "#remember-username" ? remember : error; },
        addEventListener(name, handler) { handlers[name] = handler; }
    };
    const localStorage = {
        getItem(name) {
            if (failure === "get") throw new Error("storage blocked");
            return values.has(name) ? values.get(name) : null;
        },
        setItem(name, value) {
            if (failure === "set") throw new Error("storage blocked");
            values.set(name, value);
        },
        removeItem(name) {
            if (failure === "remove") throw new Error("storage blocked");
            values.delete(name);
        }
    };
    const window = { addEventListener() {} };
    const document = { addEventListener() {} };
    vm.runInNewContext(script, { window, document, localStorage, console: { error() {} } });
    window.smartClassRememberUsername.initialize(form);
    return { values, handlers, username, remember, error };
}

test("saves the entered username only when selected and restores it", () => {
    const first = setup();
    assert.equal(first.remember.disabled, false);
    first.username.value = " teacher@example.com ";
    first.remember.checked = true;
    first.handlers.submit({ preventDefault() { assert.fail("submission prevented"); } });
    assert.equal(first.values.get(key), "teacher@example.com");
    assert.equal(first.values.size, 1);
    const next = setup(first.values.get(key));
    assert.equal(next.username.value, "teacher@example.com");
    assert.equal(next.remember.checked, true);
});

test("unchecking immediately removes the remembered username", () => {
    const state = setup("teacher");
    state.remember.checked = false;
    state.handlers.change();
    assert.equal(state.values.has(key), false);
});

test("submitting without selection removes old values", () => {
    const state = setup();
    state.values.set(key, "old");
    state.handlers.submit({ preventDefault() { assert.fail("submission prevented"); } });
    assert.equal(state.values.has(key), false);
});

test("reports blocked storage and leaves the option disabled", () => {
    const state = setup(null, "get");
    assert.equal(state.remember.disabled, true);
    assert.match(state.error.textContent, /unavailable/i);
});

test("reports save and removal failures instead of silently continuing", () => {
    const save = setup(null, "set");
    save.remember.checked = true;
    save.username.value = "teacher";
    let prevented = false;
    save.handlers.submit({ preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.match(save.error.textContent, /unavailable/i);

    const removal = setup("teacher", "remove");
    removal.remember.checked = false;
    removal.handlers.change();
    assert.match(removal.error.textContent, /unavailable/i);
    assert.equal(removal.values.get(key), "teacher");
});
