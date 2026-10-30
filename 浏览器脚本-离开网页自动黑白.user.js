// ==UserScript==
// @name         离开网页自动变黑白
// @namespace    amo.wintrans
// @version      2.0.0
// @description  按 Alt+G 给当前标签页开启；开启后鼠标移出网页（或切走窗口/标签页）自动变黑白，移回恢复。状态按标签页记忆。
// @author       阿墨
// @match        *://*/*
// @run-at       document-idle
// @noframes
// @grant        none
// ==/UserScript==

(function () {
    'use strict';

    // ============ 可改的配置 ============
    // 快捷键（默认 Alt+G）
    var HOTKEY = { alt: true, ctrl: false, shift: false, key: 'g' };

    // 新标签页默认是否开启。false = 默认关，按快捷键才开（只影响你按过的那个标签页）
    var ENABLED_BY_DEFAULT = false;
    // ===================================

    var MARK_ON = 'amoGrayOn';
    var MARK_OFF = 'amoGrayOff';
    var CSS_ID = 'amo-gray-on-leave-css';
    var CLASS = 'amo-gray-on-leave';

    var isGray = false;

    // ------------------------------------------------------------------
    //  按标签页记状态
    //  window.name 跟着标签页走，跨站跳转也不会丢；
    //  sessionStorage 是按网站隔离的，跳个站就没了，所以不用它。
    // ------------------------------------------------------------------
    function tabEnabled() {
        var n = window.name || '';
        if (n.indexOf(MARK_OFF) >= 0) return false;
        if (n.indexOf(MARK_ON) >= 0) return true;
        return ENABLED_BY_DEFAULT;
    }

    function setTabEnabled(on) {
        var n = (window.name || '').replace(MARK_ON, '').replace(MARK_OFF, '').trim();
        window.name = (n ? n + ' ' : '') + (on ? MARK_ON : MARK_OFF);
    }

    // ------------------------------------------------------------------
    //  灰度
    // ------------------------------------------------------------------
    function injectCss() {
        if (document.getElementById(CSS_ID)) return;
        var s = document.createElement('style');
        s.id = CSS_ID;
        s.textContent = 'html.' + CLASS + '{filter:grayscale(1)!important;}';
        (document.head || document.documentElement).appendChild(s);
    }

    function setGray(on) {
        if (isGray === on) return;
        isGray = on;
        injectCss();
        document.documentElement.classList.toggle(CLASS, on);
    }

    function restore() {
        if (isGray) setGray(false);
    }

    // ------------------------------------------------------------------
    //  触发条件
    // ------------------------------------------------------------------
    document.documentElement.addEventListener('mouseleave', function () {
        if (tabEnabled()) setGray(true);
    });
    document.documentElement.addEventListener('mouseenter', restore);

    window.addEventListener('blur', function () {
        if (tabEnabled()) setGray(true);
    });
    window.addEventListener('focus', restore);

    document.addEventListener('visibilitychange', function () {
        if (!tabEnabled()) return;
        if (document.hidden) setGray(true);
        else restore();
    });

    // ------------------------------------------------------------------
    //  快捷键：开关当前标签页
    // ------------------------------------------------------------------
    window.addEventListener('keydown', function (e) {
        if (!!HOTKEY.alt !== e.altKey) return;
        if (!!HOTKEY.ctrl !== e.ctrlKey) return;
        if (!!HOTKEY.shift !== e.shiftKey) return;
        if (e.metaKey) return;
        if (e.key.toLowerCase() !== HOTKEY.key) return;

        e.preventDefault();
        e.stopPropagation();

        var on = !tabEnabled();
        setTabEnabled(on);
        if (!on) restore();
        toast(on ? '本页已开启：鼠标移出网页会变黑白' : '本页已关闭');
    }, true);

    // ------------------------------------------------------------------
    //  提示
    // ------------------------------------------------------------------
    var toastTimer = null;
    function toast(text) {
        var el = document.getElementById('amo-gray-toast');
        if (!el) {
            el = document.createElement('div');
            el.id = 'amo-gray-toast';
            el.style.cssText =
                'position:fixed;left:50%;bottom:36px;transform:translateX(-50%);' +
                'background:rgba(20,20,24,.92);color:#fff;padding:8px 16px;border-radius:8px;' +
                'font:14px/1.4 "Microsoft YaHei UI","PingFang SC",sans-serif;' +
                'z-index:2147483647;pointer-events:none;opacity:0;transition:opacity .25s;' +
                'box-shadow:0 4px 16px rgba(0,0,0,.3);';
            (document.body || document.documentElement).appendChild(el);
        }
        el.textContent = text;
        el.style.opacity = '1';
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { el.style.opacity = '0'; }, 1800);
    }
})();
