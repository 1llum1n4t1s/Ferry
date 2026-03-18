// === 絵文字ピッカー ===
const EmojiPicker = {
    // カテゴリごとの絵文字リスト
    categories: {
        '\u{1F600} スマイル': ['\u{1F600}','\u{1F603}','\u{1F604}','\u{1F601}','\u{1F606}','\u{1F605}','\u{1F923}','\u{1F602}','\u{1F642}','\u{1F643}','\u{1F609}','\u{1F60A}','\u{1F607}','\u{1F970}','\u{1F60D}','\u{1F929}','\u{1F618}','\u{1F617}','\u{1F61A}','\u{1F619}','\u{1F972}','\u{1F60B}','\u{1F61B}','\u{1F61C}','\u{1F92A}','\u{1F61D}','\u{1F911}','\u{1F917}','\u{1F92D}','\u{1FAE2}','\u{1F92B}','\u{1F914}','\u{1FAE1}','\u{1F910}','\u{1F928}','\u{1F610}','\u{1F611}','\u{1F636}','\u{1FAE5}','\u{1F60F}','\u{1F612}','\u{1F644}','\u{1F62C}','\u{1F925}','\u{1F60C}','\u{1F614}','\u{1F62A}','\u{1F924}','\u{1F634}','\u{1F637}','\u{1F912}','\u{1F915}','\u{1F922}','\u{1F92E}','\u{1F975}','\u{1F976}','\u{1F974}','\u{1F635}','\u{1F92F}','\u{1F920}','\u{1F973}','\u{1F978}','\u{1F60E}','\u{1F913}','\u{1F9D0}','\u{1F615}','\u{1FAE4}','\u{1F61F}','\u{1F641}','\u{1F62E}','\u{1F62F}','\u{1F632}','\u{1F633}','\u{1F97A}','\u{1F979}','\u{1F626}','\u{1F627}','\u{1F628}','\u{1F630}','\u{1F625}','\u{1F622}','\u{1F62D}','\u{1F631}','\u{1F616}','\u{1F623}','\u{1F61E}','\u{1F613}','\u{1F629}','\u{1F62B}','\u{1F971}','\u{1F624}','\u{1F621}','\u{1F620}','\u{1F92C}','\u{1F608}','\u{1F47F}','\u{1F480}','\u2620\uFE0F','\u{1F4A9}','\u{1F921}','\u{1F479}','\u{1F47A}','\u{1F47B}','\u{1F47D}','\u{1F47E}','\u{1F916}'],
        '\u{1F44B} 手': ['\u{1F44B}','\u{1F91A}','\u{1F590}\uFE0F','\u270B','\u{1F596}','\u{1FAF1}','\u{1FAF2}','\u{1FAF3}','\u{1FAF4}','\u{1F44C}','\u{1F90C}','\u{1F90F}','\u270C\uFE0F','\u{1F91E}','\u{1FAF0}','\u{1F91F}','\u{1F918}','\u{1F919}','\u{1F448}','\u{1F449}','\u{1F446}','\u{1F595}','\u{1F447}','\u261D\uFE0F','\u{1FAF5}','\u{1F44D}','\u{1F44E}','\u270A','\u{1F44A}','\u{1F91B}','\u{1F91C}','\u{1F44F}','\u{1F64C}','\u{1FAF6}','\u{1F450}','\u{1F932}','\u{1F91D}','\u{1F64F}'],
        '\u2764\uFE0F \u30CF\u30FC\u30C8': ['\u2764\uFE0F','\u{1F9E1}','\u{1F49B}','\u{1F49A}','\u{1F499}','\u{1F49C}','\u{1F5A4}','\u{1F90D}','\u{1F90E}','\u{1F494}','\u2764\uFE0F\u200D\u{1F525}','\u2764\uFE0F\u200D\u{1FA79}','\u2763\uFE0F','\u{1F495}','\u{1F49E}','\u{1F493}','\u{1F497}','\u{1F496}','\u{1F498}','\u{1F49D}'],
        '\u{1F436} 動物': ['\u{1F436}','\u{1F431}','\u{1F42D}','\u{1F439}','\u{1F430}','\u{1F98A}','\u{1F43B}','\u{1F43C}','\u{1F43B}\u200D\u2744\uFE0F','\u{1F428}','\u{1F42F}','\u{1F981}','\u{1F42E}','\u{1F437}','\u{1F438}','\u{1F435}','\u{1F414}','\u{1F427}','\u{1F426}','\u{1F985}','\u{1F986}','\u{1F989}','\u{1F987}','\u{1F43A}','\u{1F417}','\u{1F434}','\u{1F984}','\u{1F41D}','\u{1F41B}','\u{1F98B}','\u{1F40C}','\u{1F41E}','\u{1F41C}','\u{1FAB2}','\u{1F422}','\u{1F40D}','\u{1F98E}','\u{1F996}','\u{1F995}','\u{1F419}','\u{1F991}','\u{1F980}','\u{1F421}','\u{1F420}','\u{1F41F}','\u{1F42C}','\u{1F433}','\u{1F40B}','\u{1F988}','\u{1F40A}'],
        '\u{1F354} 食べ物': ['\u{1F34F}','\u{1F34E}','\u{1F350}','\u{1F34A}','\u{1F34B}','\u{1F34C}','\u{1F349}','\u{1F347}','\u{1F353}','\u{1FAD0}','\u{1F348}','\u{1F352}','\u{1F351}','\u{1F96D}','\u{1F34D}','\u{1F965}','\u{1F95D}','\u{1F345}','\u{1F346}','\u{1F951}','\u{1F966}','\u{1F96C}','\u{1F952}','\u{1F336}\uFE0F','\u{1FAD1}','\u{1F33D}','\u{1F955}','\u{1FAD2}','\u{1F9C4}','\u{1F9C5}','\u{1F954}','\u{1F360}','\u{1FAD8}','\u{1F950}','\u{1F35E}','\u{1F956}','\u{1F968}','\u{1F9C0}','\u{1F95A}','\u{1F373}','\u{1F9C8}','\u{1F95E}','\u{1F9C7}','\u{1F953}','\u{1F969}','\u{1F357}','\u{1F356}','\u{1F9B4}','\u{1F32D}','\u{1F354}','\u{1F35F}','\u{1F355}','\u{1FAD3}','\u{1F96A}','\u{1F959}','\u{1F9C6}','\u{1F32E}','\u{1F32F}','\u{1FAD4}','\u{1F957}','\u{1F958}','\u{1FAD5}','\u{1F35D}','\u{1F35C}','\u{1F372}','\u{1F35B}','\u{1F363}','\u{1F371}','\u{1F95F}','\u{1F9AA}','\u{1F364}','\u{1F359}','\u{1F35A}','\u{1F358}','\u{1F365}','\u{1F960}','\u{1F96E}','\u{1F362}','\u{1F361}','\u{1F367}','\u{1F368}','\u{1F366}','\u{1F967}','\u{1F9C1}','\u{1F370}','\u{1F382}','\u{1F36E}','\u{1F36D}','\u{1F36C}','\u{1F36B}','\u{1F37F}','\u{1F369}','\u{1F36A}','\u{1F330}','\u{1F95C}','\u{1F36F}'],
        '\u26BD スポーツ': ['\u26BD','\u{1F3C0}','\u{1F3C8}','\u26BE','\u{1F94E}','\u{1F3BE}','\u{1F3D0}','\u{1F3C9}','\u{1F94F}','\u{1F3B1}','\u{1FA80}','\u{1F3D3}','\u{1F3F8}','\u{1F3D2}','\u{1F3D1}','\u{1F94D}','\u{1F3CF}','\u{1FA83}','\u{1F945}','\u26F3','\u{1FA81}','\u{1F3F9}','\u{1F3A3}','\u{1F93F}','\u{1F94A}','\u{1F94B}','\u{1F3BD}','\u{1F6F9}','\u{1F6FC}','\u{1F6F7}','\u26F8\uFE0F','\u{1F94C}','\u{1F3BF}','\u26F7\uFE0F','\u{1F3C2}'],
        '\u{1F3E0} その他': ['\u{1F3E0}','\u{1F3E2}','\u{1F3E5}','\u{1F3E6}','\u{1F3EB}','\u{1F3EA}','\u{1F3E8}','\u{1F492}','\u26EA','\u{1F54C}','\u{1F6D5}','\u{1F54D}','\u26E9\uFE0F','\u{1F54B}','\u26F2','\u26FA','\u{1F3D5}\uFE0F','\u{1F5FC}','\u{1F5FD}','\u{1F5FB}','\u{1F30B}','\u{1F3D4}\uFE0F','\u26F0\uFE0F','\u{1F3DC}\uFE0F','\u{1F3D6}\uFE0F','\u{1F3DD}\uFE0F','\u{1F305}','\u{1F304}','\u{1F320}','\u{1F387}','\u{1F386}','\u{1F307}','\u{1F306}','\u{1F3D9}\uFE0F','\u{1F303}','\u{1F30C}','\u{1F309}','\u{1F301}'],
    },

    _visible: false,
    _el: null,
    _currentCategory: null,

    // ピッカー要素を生成
    _createEl() {
        const picker = document.createElement('div');
        picker.className = 'emoji-picker';
        picker.innerHTML = `
            <div class="emoji-search">
                <input type="text" id="emoji-search-input" placeholder="\u7D75\u6587\u5B57\u3092\u691C\u7D22...">
            </div>
            <div class="emoji-categories" id="emoji-categories"></div>
            <div class="emoji-grid" id="emoji-grid"></div>
        `;
        document.body.appendChild(picker);
        this._el = picker;

        // カテゴリタブを生成
        const catContainer = picker.querySelector('#emoji-categories');
        const catNames = Object.keys(this.categories);
        catNames.forEach((name, i) => {
            const btn = document.createElement('button');
            // カテゴリ名の先頭の絵文字をタブに使用
            btn.textContent = name.split(' ')[0];
            btn.title = name;
            if (i === 0) btn.classList.add('active');
            btn.addEventListener('click', () => this._selectCategory(name));
            catContainer.appendChild(btn);
        });

        // 初期カテゴリを表示
        this._currentCategory = catNames[0];
        this._renderGrid(this.categories[catNames[0]]);

        // 検索イベント
        picker.querySelector('#emoji-search-input').addEventListener('input', (e) => {
            this.search(e.target.value);
        });

        // ピッカー外クリックで閉じる
        document.addEventListener('click', (e) => {
            if (this._visible && !this._el.contains(e.target) && e.target.id !== 'btn-emoji') {
                this.hide();
            }
        });

        return picker;
    },

    // カテゴリ選択
    _selectCategory(name) {
        this._currentCategory = name;
        const tabs = this._el.querySelectorAll('.emoji-categories button');
        const catNames = Object.keys(this.categories);
        tabs.forEach((btn, i) => {
            btn.classList.toggle('active', catNames[i] === name);
        });
        this._renderGrid(this.categories[name]);
        // 検索欄をクリア
        this._el.querySelector('#emoji-search-input').value = '';
    },

    // 絵文字グリッドを描画
    _renderGrid(emojis) {
        const grid = this._el.querySelector('#emoji-grid');
        grid.innerHTML = '';
        const frag = document.createDocumentFragment();
        for (const emoji of emojis) {
            const btn = document.createElement('button');
            btn.textContent = emoji;
            btn.addEventListener('click', () => this.insertEmoji(emoji));
            frag.appendChild(btn);
        }
        grid.appendChild(frag);
    },

    // ピッカー表示
    show(anchorEl) {
        if (!this._el) this._createEl();
        const rect = anchorEl.getBoundingClientRect();
        // ボタンの上方向に展開
        this._el.style.left = Math.max(0, rect.left - 160) + 'px';
        this._el.style.bottom = (window.innerHeight - rect.top + 4) + 'px';
        this._el.style.top = 'auto';
        this._el.style.display = 'flex';
        this._visible = true;
    },

    // ピッカー非表示
    hide() {
        if (this._el) {
            this._el.style.display = 'none';
            this._visible = false;
        }
    },

    // 絵文字検索（全カテゴリから部分一致）
    search(query) {
        if (!query) {
            this._renderGrid(this.categories[this._currentCategory]);
            return;
        }
        const q = query.toLowerCase();
        const results = [];
        for (const emojis of Object.values(this.categories)) {
            for (const emoji of emojis) {
                // 絵文字自体にマッチ（そのまま入力された場合）
                if (emoji.includes(q)) results.push(emoji);
            }
        }
        this._renderGrid(results);
    },

    // テキストエリアに絵文字を挿入
    insertEmoji(emoji) {
        const input = document.getElementById('chat-input');
        if (!input) return;
        const start = input.selectionStart;
        const end = input.selectionEnd;
        const text = input.value;
        input.value = text.substring(0, start) + emoji + text.substring(end);
        input.selectionStart = input.selectionEnd = start + emoji.length;
        input.focus();
    },

    // トグル表示
    toggle(anchorEl) {
        if (this._visible) {
            this.hide();
        } else {
            this.show(anchorEl);
        }
    }
};
