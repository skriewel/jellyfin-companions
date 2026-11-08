/* Companions – soundtracks and books for movies and series.
 * Movie/series page (and its seasons and episodes): a "Soundtrack" row with the albums from the music
 * library and a "Book" row with the books and audiobooks from the configured book servers.
 * Album page: the movies and series the album is the soundtrack of.
 * Rows use jellyfin-web's own markup (verticalSection, emby-scroller, card), so themes style them like
 * every other row. Album and movie cards are handled by jellyfin-web; book cards are plain links. */
(function () {
    'use strict';

    var cfg = window.CompanionsConfig || {};
    if (window.Companions || cfg.enabled === false) return;

    var SECTION_CLASS = 'cmp-section';
    var de = /^de\b/i.test(document.documentElement.lang || navigator.language || '');
    var T = de ? {
        soundtrack: 'Soundtrack', soundtracks: 'Soundtracks', seriesSoundtrack: 'Soundtrack zur Serie',
        book: 'Buchvorlage', books: 'Bücher zum Film', seriesBooks: 'Bücher zur Serie', seriesBook: 'Buchvorlage der Serie',
        movie: 'Zum Film', series: 'Zur Serie', titles: 'Filme und Serien',
        ebook: 'E-Book', audiobook: 'Hörbuch', comic: 'Comic', open: 'Öffnen in', volumes: function (n) { return n + ' Bände'; }
    } : {
        soundtrack: 'Soundtrack', soundtracks: 'Soundtracks', seriesSoundtrack: 'Soundtrack of the series',
        book: 'Based on', books: 'Books', seriesBooks: 'Books', seriesBook: 'The series is based on',
        movie: 'The movie', series: 'The show', titles: 'Movies and shows',
        ebook: 'E-book', audiobook: 'Audiobook', comic: 'Comic', open: 'Open in', volumes: function (n) { return n + ' volumes'; }
    };

    var cache = {};      // itemId -> Promise<data>
    var renderSeq = 0;

    function api() { return window.ApiClient; }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (ch) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch];
        });
    }

    function currentItemId() {
        var h = location.hash || '';
        if (!/^#\/?details\b/i.test(h)) return null;
        var m = /[?&]id=([0-9a-f-]{32,36})/i.exec(h);
        return m ? m[1].replace(/-/g, '').toLowerCase() : null;
    }

    function activePage() {
        var pages = document.querySelectorAll('.itemDetailPage');
        for (var i = pages.length - 1; i >= 0; i--) {
            if (!pages[i].classList.contains('hide')) return pages[i];
        }
        return null;
    }

    function fetchData(id) {
        if (!cache[id]) {
            var c = api();
            cache[id] = c.getJSON(c.getUrl('Companions/Item/' + id)).catch(function (e) {
                delete cache[id];
                throw e;
            });
        }
        return cache[id];
    }

    function fetchItems(ids) {
        if (!ids || !ids.length) return Promise.resolve([]);
        var c = api();
        return c.getItems(c.getCurrentUserId(), {
            Ids: ids.join(','),
            Fields: 'PrimaryImageAspectRatio,ProductionYear,AlbumArtist',
            EnableUserData: true,
            EnableImageTypes: 'Primary'
        }).then(function (r) {
            var byId = {};
            (r.Items || []).forEach(function (it) { byId[it.Id] = it; });
            return ids.map(function (id) { return byId[String(id).replace(/-/g, '')] || byId[id]; }).filter(Boolean);
        });
    }

    // ------------------------------------------------------------------ cards

    function itemCard(item, shape) {
        var c = api();
        var serverId = item.ServerId || c.serverId();
        var href = '#/details?id=' + item.Id + '&serverId=' + serverId;
        var tag = item.ImageTags && item.ImageTags.Primary;
        var img = tag ? c.getScaledImageUrl(item.Id, { type: 'Primary', tag: tag, maxWidth: 300 }) : '';
        var secondary = item.Type === 'MusicAlbum'
            ? (item.AlbumArtist || (item.AlbumArtists && item.AlbumArtists[0] && item.AlbumArtists[0].Name) || item.ProductionYear || '')
            : (item.ProductionYear || '');
        var icon = item.Type === 'MusicAlbum' ? 'album' : item.Type === 'Series' ? 'tv' : 'movie';
        var playButton = '<button is="paper-icon-button-light" class="cardOverlayButton cardOverlayButton-hover itemAction paper-icon-button-light cardOverlayFab-primary" data-action="resume" title="Play">'
            + '<span class="material-icons cardOverlayButtonIcon cardOverlayButtonIcon-hover play_arrow" aria-hidden="true"></span></button>';
        return '<div class="card ' + shape + 'Card card-hoverable card-withuserdata cmp-card" data-index="0"'
            + ' data-id="' + esc(item.Id) + '" data-serverid="' + esc(serverId) + '" data-type="' + esc(item.Type) + '"'
            + ' data-mediatype="' + esc(item.MediaType || '') + '" data-isfolder="' + (item.IsFolder ? 'true' : 'false') + '" data-action="link">'
            + '<div class="cardBox cardBox-bottompadded"><div class="cardScalable">'
            + '<div class="cardPadder cardPadder-' + shape + '"></div>'
            + '<a href="' + esc(href) + '" data-action="link" class="cardImageContainer coveredImage cardContent itemAction"'
            + (img ? ' style="background-image:url(\'' + esc(img) + '\')"' : '') + ' aria-label="' + esc(item.Name) + '">'
            + (img ? '' : '<span class="cardImageIcon material-icons ' + icon + '" aria-hidden="true"></span>') + '</a>'
            + '<div class="cardOverlayContainer itemAction" data-action="link"><a href="' + esc(href) + '" class="cardImageContainer"></a>' + playButton + '</div>'
            + '</div>'
            + '<div class="cardText cardTextCentered cardText-first"><bdi>' + esc(item.Name) + '</bdi></div>'
            + '<div class="cardText cardTextCentered cardText-secondary"><bdi>' + esc(secondary) + '</bdi></div>'
            + '</div></div>';
    }

    function bookCard(b) {
        var c = api();
        var img = b.cover ? c.getUrl(b.cover) : '';
        var icon = b.kind === 'audiobook' ? 'headphones' : b.kind === 'comic' ? 'auto_stories' : 'menu_book';
        var kind = T[b.kind] || T.ebook;
        var where = (b.volumes ? T.volumes(b.volumes) + ' · ' : '') + (b.server ? kind + ' · ' + b.server : kind);
        var label = b.title + (b.authors && b.authors.length ? ' – ' + b.authors.join(', ') : '') + ' (' + T.open + ' ' + (b.server || '') + ')';
        var tagOpen = b.url ? '<a href="' + esc(b.url) + '" target="_blank" rel="noopener noreferrer"' : '<div';
        var tagClose = b.url ? '</a>' : '</div>';
        // audiobook covers are square
        var shape = b.kind === 'audiobook' ? 'overflowSquare' : 'overflowPortrait';
        return '<div class="card ' + shape + 'Card card-hoverable cmp-card cmp-book" data-kind="' + esc(b.kind) + '">'
            + '<div class="cardBox cardBox-bottompadded">'
            + tagOpen + ' class="cmp-book-link" title="' + esc(label) + '" aria-label="' + esc(label) + '">'
            + '<div class="cardScalable">'
            + '<div class="cardPadder cardPadder-' + shape + '"></div>'
            + '<div class="cardImageContainer coveredImage cardContent cmp-cover">'
            + '<span class="cardImageIcon material-icons ' + icon + '" aria-hidden="true"></span>'
            + (img ? '<img class="cmp-cover-img" alt="" loading="lazy" src="' + esc(img) + '" onerror="this.remove()">' : '')
            + '<span class="cmp-badge material-icons ' + icon + '" aria-hidden="true"></span>'
            + '</div></div>'
            + '<div class="cardText cardTextCentered cardText-first"><bdi>' + esc(b.title) + '</bdi></div>'
            + '<div class="cardText cardTextCentered cardText-secondary"><bdi>' + esc(where) + '</bdi></div>'
            + tagClose + '</div></div>';
    }

    function sectionHtml(title, cardsHtml, kind) {
        // Book cards are links, not Jellyfin items: their row is not an "itemsContainer", so scripts of
        // other plugins that add cards to Jellyfin's item rows leave it alone.
        var container = kind === 'books'
            ? '<div class="scrollSlider focuscontainer-x cmp-slider">'
            : '<div is="emby-itemscontainer" class="scrollSlider focuscontainer-x itemsContainer">';
        return '<div class="verticalSection detailVerticalSection ' + SECTION_CLASS + '" data-cmp-kind="' + kind + '">'
            + '<h2 class="sectionTitle sectionTitle-cards padded-right">' + esc(title) + '</h2>'
            + '<div is="emby-scroller" class="padded-top-focusscale padded-bottom-focusscale no-padding" data-centerfocus="true">'
            + container + cardsHtml + '</div>'
            + '</div></div>';
    }

    // ------------------------------------------------------------------ placement

    function removeSections(root) {
        (root || document).querySelectorAll('.' + SECTION_CLASS).forEach(function (n) { n.remove(); });
    }

    function insertSections(page, html, itemId) {
        var container = page.querySelector('.detailPageSecondaryContainer');
        if (!container) return;
        removeSections(page);
        var wrap = document.createElement('div');
        wrap.innerHTML = html;
        // after seasons/episodes/tracks and other rows of the title itself, before cast and "more like this"
        var before = container.querySelector('.moreFromSeasonSection')
            || container.querySelector('#castCollapsible')
            || container.querySelector('#similarCollapsible')
            || null;
        Array.prototype.slice.call(wrap.children).forEach(function (s) {
            s.setAttribute('data-cmp-item', itemId);
            container.insertBefore(s, before);
        });
    }

    function render() {
        var seq = ++renderSeq;
        var id = currentItemId();
        if (!id || !api() || !api().getCurrentUserId || !api().getCurrentUserId()) {
            removeSections();
            return;
        }

        waitForPage(seq).then(function (page) {
            if (!page || seq !== renderSeq) return;
            var existing = page.querySelector('.' + SECTION_CLASS);
            if (existing && existing.getAttribute('data-cmp-item') === id) return;
            removeSections(page);

            fetchData(id).then(function (d) {
                if (seq !== renderSeq || !d) return null;
                if (d.errors && d.errors.length) console.warn('[Companions]', d.errors.join(' | '));

                if (d.kind === 'album') {
                    return fetchItems(d.titles).then(function (items) {
                        if (seq !== renderSeq || !items.length) return;
                        var title = items.length > 1 ? T.titles : (items[0].Type === 'Series' ? T.series : T.movie);
                        insertSections(page, sectionHtml(title, items.map(function (it) { return itemCard(it, 'overflowPortrait'); }).join(''), 'titles'), id);
                    });
                }

                if (d.kind !== 'movie' && d.kind !== 'series') return null;
                return fetchItems(d.soundtracks).then(function (albums) {
                    if (seq !== renderSeq) return;
                    var parent = d.scope === 'parentSeries';
                    var html = '';
                    if (albums.length) {
                        var st = parent ? T.seriesSoundtrack : (albums.length > 1 ? T.soundtracks : T.soundtrack);
                        html += sectionHtml(st, albums.map(function (a) { return itemCard(a, 'overflowSquare'); }).join(''), 'soundtrack');
                    }
                    if (d.books && d.books.length) {
                        var bt = parent || d.kind === 'series'
                            ? (d.books.length > 1 ? T.seriesBooks : T.seriesBook)
                            : (d.books.length > 1 ? T.books : T.book);
                        html += sectionHtml(bt, d.books.map(bookCard).join(''), 'books');
                    }
                    if (html) insertSections(page, html, id);
                });
            }).catch(function (e) {
                console.warn('[Companions]', e);
            });
        });
    }

    // the details page is rendered asynchronously: wait until it is there
    function waitForPage(seq) {
        return new Promise(function (resolve) {
            var tries = 0;
            (function check() {
                if (seq !== renderSeq) return resolve(null);
                var page = activePage();
                if (page && page.querySelector('.detailPageSecondaryContainer')) return resolve(page);
                if (++tries > 100) return resolve(null);
                setTimeout(check, 100);
            })();
        });
    }

    document.addEventListener('viewshow', function () { setTimeout(render, 0); }, true);
    window.addEventListener('hashchange', function () { setTimeout(render, 0); });
    setInterval(function () { cache = {}; }, 5 * 60 * 1000);
    render();

    window.Companions = { version: cfg.version, refresh: function () { cache = {}; removeSections(); render(); } };
})();
