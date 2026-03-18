// === 設定 UI ===
const Settings = {
    data: {},

    render(data) {
        this.data = data;
        const container = document.getElementById('settings-content');
        container.innerHTML = `
            <!-- 一般 -->
            <div class="settings-section">
                <div class="settings-section-title">${I18n.t('Settings.General')}</div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.DisplayName')}</div>
                    </div>
                    <input type="text" class="settings-input" id="set-display-name"
                           value="${this.escapeAttr(data.displayName)}"
                           placeholder="${I18n.t('Settings.DisplayName.Placeholder')}">
                </div>
            </div>

            <!-- 通知 -->
            <div class="settings-section">
                <div class="settings-section-title">${I18n.t('Settings.Notification')}</div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.NotificationSound')}</div>
                        <div class="settings-desc">${I18n.t('Settings.NotificationSound.Desc')}</div>
                    </div>
                    ${this.toggle('set-notification-sound', data.enableNotificationSound)}
                </div>
            </div>

            <!-- ファイル転送 -->
            <div class="settings-section">
                <div class="settings-section-title">${I18n.t('Settings.FileTransfer')}</div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.SaveDirectory')}</div>
                        <div class="settings-desc">${this.escapeHtml(data.saveDirectory)}</div>
                    </div>
                    <button class="secondary-btn" id="set-browse">${I18n.t('Settings.SaveDirectory.Browse')}</button>
                </div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.ReceiveFileSavePath')}</div>
                        <div class="settings-desc">${this.escapeHtml(data.receiveFileSavePath || I18n.t('Settings.ReceiveFileSavePath.Default'))}</div>
                    </div>
                    <button class="secondary-btn" id="set-browse-receive">${I18n.t('Settings.SaveDirectory.Browse')}</button>
                </div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.AutoAcceptFile')}</div>
                        <div class="settings-desc">${I18n.t('Settings.AutoAcceptFile.Desc')}</div>
                    </div>
                    ${this.toggle('set-auto-accept', data.autoAcceptFileTransfer)}
                </div>
                <div class="settings-row">
                    <div class="settings-label">${I18n.t('Settings.ChatRetention')}</div>
                    <select class="settings-select" id="set-retention">
                        ${(data.chatRetentionOptions || []).map(d =>
                            `<option value="${d}" ${d === data.chatRetentionDays ? 'selected' : ''}>${d}${I18n.t('Settings.ChatRetention.Days')}</option>`
                        ).join('')}
                        <option value="0" ${data.chatRetentionDays === 0 ? 'selected' : ''}>${I18n.t('Settings.ChatRetention.Unlimited')}</option>
                    </select>
                </div>
            </div>

            <!-- 外観 -->
            <div class="settings-section">
                <div class="settings-section-title">${I18n.t('Settings.Appearance')}</div>
                <div class="settings-row">
                    <div class="settings-label">${I18n.t('Settings.Theme')}</div>
                    <select class="settings-select" id="set-theme">
                        <option value="0" ${data.selectedThemeIndex === 0 ? 'selected' : ''}>${I18n.t('Settings.Theme.System')}</option>
                        <option value="1" ${data.selectedThemeIndex === 1 ? 'selected' : ''}>${I18n.t('Settings.Theme.Light')}</option>
                        <option value="2" ${data.selectedThemeIndex === 2 ? 'selected' : ''}>${I18n.t('Settings.Theme.Dark')}</option>
                    </select>
                </div>
                <div class="settings-row">
                    <div class="settings-label">${I18n.t('Settings.AccentColor')}</div>
                    <input type="color" class="settings-color-input" id="set-accent-color"
                           value="${this.escapeAttr(data.accentColor || '#007AFF')}">
                </div>
                <div class="settings-row">
                    <div class="settings-label">${I18n.t('Settings.FontSize')}</div>
                    <select class="settings-select" id="set-font-size">
                        <option value="small" ${data.fontSize === 'small' ? 'selected' : ''}>${I18n.t('Settings.FontSize.Small')}</option>
                        <option value="medium" ${data.fontSize === 'medium' ? 'selected' : ''}>${I18n.t('Settings.FontSize.Medium')}</option>
                        <option value="large" ${data.fontSize === 'large' ? 'selected' : ''}>${I18n.t('Settings.FontSize.Large')}</option>
                    </select>
                </div>
                <div class="settings-row">
                    <div class="settings-label">${I18n.t('Settings.Language')}</div>
                    <select class="settings-select" id="set-locale">
                        ${(data.localeOptions || []).map(l =>
                            `<option value="${l.key}" ${l.key === data.selectedLocale ? 'selected' : ''}>${l.displayName}</option>`
                        ).join('')}
                    </select>
                </div>
            </div>

            <!-- 動作 -->
            <div class="settings-section">
                <div class="settings-section-title">${I18n.t('Settings.Behavior')}</div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.AutoStartWithWindows')}</div>
                        <div class="settings-desc">${I18n.t('Settings.AutoStartWithWindows.Desc')}</div>
                    </div>
                    ${this.toggle('set-auto-start', data.autoStartWithWindows)}
                </div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.RunAtStartup')}</div>
                        <div class="settings-desc">${I18n.t('Settings.RunAtStartup.Desc')}</div>
                    </div>
                    ${this.toggle('set-startup', data.runAtStartup)}
                </div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.StartMinimized')}</div>
                        <div class="settings-desc">${I18n.t('Settings.StartMinimized.Desc')}</div>
                    </div>
                    ${this.toggle('set-minimized', data.startMinimized)}
                </div>
                <div class="settings-row">
                    <div>
                        <div class="settings-label">${I18n.t('Settings.MinimizeToTray')}</div>
                        <div class="settings-desc">${I18n.t('Settings.MinimizeToTray.Desc')}</div>
                    </div>
                    ${this.toggle('set-tray', data.minimizeToTray)}
                </div>
            </div>

            <!-- バージョン -->
            <div class="settings-section">
                <div class="settings-section-title">${I18n.t('Settings.Version')}</div>
                <div class="settings-row">
                    <div class="settings-label">Ferry v${this.escapeHtml(data.versionText)}</div>
                    <button class="secondary-btn" id="set-update">${I18n.t('Settings.CheckUpdate')}</button>
                </div>
            </div>
        `;

        // イベントリスナー
        this.bind('set-display-name', 'change', (e) =>
            this.save('displayName', e.target.value));
        this.bind('set-notification-sound', 'change', (e) =>
            this.save('enableNotificationSound', e.target.checked));
        this.bind('set-browse', 'click', () =>
            Bridge.send('browseSaveDir'));
        this.bind('set-browse-receive', 'click', () =>
            Bridge.send('browseReceiveFileSavePath'));
        this.bind('set-auto-accept', 'change', (e) =>
            this.save('autoAcceptFileTransfer', e.target.checked));
        this.bind('set-retention', 'change', (e) =>
            this.save('chatRetentionDays', parseInt(e.target.value)));
        this.bind('set-theme', 'change', (e) =>
            this.save('theme', parseInt(e.target.value)));
        this.bind('set-accent-color', 'change', (e) =>
            this.save('accentColor', e.target.value));
        this.bind('set-font-size', 'change', (e) =>
            this.save('fontSize', e.target.value));
        this.bind('set-locale', 'change', (e) =>
            this.save('locale', e.target.value));
        this.bind('set-auto-start', 'change', (e) =>
            this.save('autoStartWithWindows', e.target.checked));
        this.bind('set-startup', 'change', (e) =>
            this.save('runAtStartup', e.target.checked));
        this.bind('set-minimized', 'change', (e) =>
            this.save('startMinimized', e.target.checked));
        this.bind('set-tray', 'change', (e) =>
            this.save('minimizeToTray', e.target.checked));
        this.bind('set-update', 'click', () =>
            Bridge.send('checkUpdate'));
    },

    save(key, value) {
        Bridge.send('saveSetting', { key, value });
    },

    toggle(id, checked) {
        return `<label class="toggle-switch">
            <input type="checkbox" id="${id}" ${checked ? 'checked' : ''}>
            <span class="toggle-slider"></span>
        </label>`;
    },

    bind(id, event, handler) {
        const el = document.getElementById(id);
        if (el) el.addEventListener(event, handler);
    },

    escapeHtml(text) {
        const el = document.createElement('span');
        el.textContent = text || '';
        return el.innerHTML;
    },

    escapeAttr(text) {
        return (text || '').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }
};

// C# からのイベント
Bridge.on('loadSettings', (data) => Settings.render(data));
