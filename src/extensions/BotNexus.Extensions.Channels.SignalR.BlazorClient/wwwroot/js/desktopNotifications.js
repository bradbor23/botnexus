// desktopNotifications.js -- OS-level toasts for gateway notifications.
//
// The bell in the banner answers "what happened?" for someone who is looking at the portal. This
// answers it for someone who is not: an agent that fails at 2am, or a run that blocks waiting for
// input, should reach the person who started it without them having to keep a tab in front of
// them. That is the whole point of the notification manager.
//
// Three facts shape this file:
//
//   1. Permission can only be asked for from a user gesture. A prompt raised on page load is
//      ignored by Chrome and refused outright by Safari, and a permission that has been DENIED
//      can never be asked for again from script -- the user has to clear it in browser settings.
//      So the request is wired to a button in the panel, never to startup.
//   2. Permission is not the same as consent. A browser may have granted the origin permission
//      long ago for something else; that is not the user asking THIS portal to shout at them.
//      An explicit opt-in is kept alongside it, in localStorage, because it belongs to the
//      browser exactly like the permission does -- not to the gateway, and not to the account.
//   3. localStorage throws, it does not merely return null, when site data is blocked. Every
//      access here is guarded; a browser that refuses storage degrades to "off", not to an
//      exception thrown out of a banner that renders on every page.
window.botnexusDesktopNotifications = window.botnexusDesktopNotifications || {
    _ref: null,
    _key: 'botnexus.desktopNotifications',

    _supported: function () {
        return typeof window.Notification === 'function';
    },

    _enabled: function () {
        try {
            return window.localStorage.getItem(this._key) === 'on';
        } catch (e) {
            return false;
        }
    },

    status: function () {
        return {
            supported: this._supported(),
            permission: this._supported() ? window.Notification.permission : 'unsupported',
            enabled: this._enabled()
        };
    },

    setEnabled: function (on) {
        try {
            window.localStorage.setItem(this._key, on ? 'on' : 'off');
        } catch (e) {
            // Site data blocked: the toggle cannot be remembered, so it stays off. Reporting the
            // real status back is what lets the UI say so rather than lie about being on.
        }

        return this.status();
    },

    // Called from the button in the notification panel, so it always runs inside a user gesture.
    request: async function () {
        if (!this._supported()) return this.status();

        var permission = window.Notification.permission;

        if (permission === 'default') {
            permission = await window.Notification.requestPermission();
        }

        // Granting IS the opt-in: the user just answered a prompt they asked for. Making them
        // then flip a second switch would be a dark pattern in reverse.
        if (permission === 'granted') this.setEnabled(true);

        return this.status();
    },

    // The portal hands over a reference to the notification centre so a clicked toast can route
    // inside the running app. Without it a click still works, it just costs a full reload.
    register: function (ref) { this._ref = ref; },

    unregister: function () { this._ref = null; },

    show: function (id, title, body, link) {
        if (!this._supported() || window.Notification.permission !== 'granted' || !this._enabled()) {
            return 'inactive';
        }

        // Someone watching the portal has already been told: the badge moved and the list is one
        // click away. An OS toast on top of that is noise, and noise is how notifications get
        // switched off. The toast is for when the portal is NOT what you are looking at.
        if (document.visibilityState === 'visible' && document.hasFocus()) {
            return 'suppressed-visible';
        }

        try {
            var toast = new window.Notification(title, {
                body: body || '',
                // The notification id as tag: a re-push of the same notification REPLACES its
                // toast instead of stacking a second copy of the same news.
                tag: id,
                icon: document.baseURI + 'icon-192.png'
            });

            var self = this;

            toast.onclick = function () {
                window.focus();
                toast.close();

                if (!link) return;

                if (self._ref) {
                    // Routed inside the SPA - clicking a toast must not reboot the WASM app.
                    self._ref.invokeMethodAsync('OpenFromDesktopNotification', link).catch(function () {
                        window.location.href = link;
                    });
                } else {
                    window.location.href = link;
                }
            };

            return 'shown';
        } catch (e) {
            // Android Chrome throws here by design: it permits notifications only through a
            // service worker. That is the web-push path, not this one, so this is an expected
            // failure on those browsers rather than a defect.
            console.warn('[BotNexus] desktop notification failed:', e);
            return 'failed';
        }
    }
};
