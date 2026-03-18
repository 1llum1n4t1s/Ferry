// === 国際化（i18n）===
const I18n = {
    texts: {},
    locale: 'en_US',

    // テキスト取得
    t(key) {
        return this.texts[key] || `Text.${key}`;
    },

    // テキスト読み込み
    load(locale, texts) {
        this.locale = locale;
        this.texts = texts;
        this.updateAll();
    },

    // data-i18n 属性を持つ要素を全て更新
    updateAll() {
        document.querySelectorAll('[data-i18n]').forEach(el => {
            const key = el.getAttribute('data-i18n');
            el.textContent = this.t(key);
        });
        document.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
            const key = el.getAttribute('data-i18n-placeholder');
            el.placeholder = this.t(key);
        });
        document.querySelectorAll('[data-i18n-title]').forEach(el => {
            const key = el.getAttribute('data-i18n-title');
            el.title = this.t(key);
        });
    }
};

// C# からテキスト受信
Bridge.on('loadTexts', (data) => {
    I18n.load(data.locale, data.texts);
});
