// Anchor the conversation switcher panel to its trigger.
//
// The panel CANNOT be positioned with `position: absolute` inside the chat header. Three of its
// ancestors - .chat-title-heading-row, .chat-title-stack and .chat-header - carry `overflow: hidden`
// so a long conversation title truncates instead of painting beneath the header action group
// (#2141, #2441). Those rules are load-bearing, and an absolutely positioned descendant is clipped
// by every one of them, so the panel rendered with a correct 352x423 box and was shaved to nothing
// by a ~28px tall header.
//
// `position: fixed` escapes all three, because none of those ancestors establishes a containing
// block (no transform, filter or will-change). The trade is that fixed offsets are viewport-relative,
// so the panel has to be told where its trigger is - which is all this file does.
window.botnexusConversationSwitcher = {
    // Places `panel` under `trigger`, flipping above it when there is not enough room below and
    // clamping horizontally so a switcher near the right edge stays on screen.
    anchor: function (trigger, panel) {
        if (!trigger || !panel) return;

        const margin = 8;
        const gap = 4;
        const r = trigger.getBoundingClientRect();

        const width = panel.offsetWidth;
        const left = Math.min(Math.max(margin, r.left), Math.max(margin, window.innerWidth - width - margin));

        const height = panel.offsetHeight;
        const below = r.bottom + gap;
        const fitsBelow = below + height <= window.innerHeight - margin;
        const top = fitsBelow ? below : Math.max(margin, r.top - gap - height);

        panel.style.left = left + 'px';
        panel.style.top = top + 'px';
    }
};
