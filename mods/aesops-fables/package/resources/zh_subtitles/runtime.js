(function () {
  'use strict';
  if (window.GameZhSubtitles) return;
  var fs = require('fs');
  var path = require('path');
  var url = require('url');
  var root = path.dirname(url.fileURLToPath(document.currentScript.src));
  var config, database;
  try {
    config = JSON.parse(fs.readFileSync(path.join(root, 'config.json'), 'utf8'));
    database = JSON.parse(fs.readFileSync(path.join(root, 'subtitles.zh-CN.json'), 'utf8'));
  } catch (error) {
    console.error('[中文字幕] 加载失败', error);
    return;
  }
  var tracks = new Map();
  var sequence = 0;
  var element = null;
  var lastText = '';
  var enabled = config.enabled !== false;
  function normalize(value) {
    try { value = decodeURIComponent(String(value || '')); } catch (_) { value = String(value || ''); }
    value = value.replace(/\\/g, '/').split(/[?#]/)[0];
    var start = value.lastIndexOf('/data/');
    if (start >= 0) value = value.slice(start + 1);
    return value.replace(/^\.\//, '');
  }
  function cuesFor(value) {
    return database.media[normalize(value)] || null;
  }
  function cueAt(cues, time) {
    if (!Number.isFinite(time)) return '';
    var low = 0, high = cues.length - 1, found = -1;
    while (low <= high) {
      var middle = (low + high) >> 1;
      if (cues[middle].start <= time) { found = middle; low = middle + 1; }
      else high = middle - 1;
    }
    if (found >= 0 && time < cues[found].end) return cues[found].text;
    return '';
  }
  function show(text) {
    var base = document.getElementById('tyrano_base');
    if (!base) return;
    if (!element || !element.isConnected) {
      element = document.createElement('div');
      element.id = 'game-zh-subtitles';
      element.setAttribute('aria-live', 'off');
      Object.assign(element.style, {
        position: 'absolute', left: '50%', bottom: String(config.bottomOffset || 120) + 'px',
        transform: 'translateX(-50%)', maxWidth: '84%', width: 'max-content', boxSizing: 'border-box',
        padding: '7px 16px', borderRadius: '7px', background: 'rgba(0,0,0,0.72)',
        color: '#fff', fontFamily: '"Microsoft YaHei", "Noto Sans CJK SC", sans-serif',
        fontSize: String(config.fontSize || 28) + 'px', lineHeight: '1.4', textAlign: 'center',
        whiteSpace: 'pre-line', overflowWrap: 'break-word', textShadow: '0 1px 3px #000',
        pointerEvents: 'none', zIndex: '2147483600', display: 'none'
      });
      base.appendChild(element);
      lastText = '';
    }
    if (text !== lastText) { element.textContent = text; lastText = text; }
    element.style.display = text ? 'block' : 'none';
  }
  function menuOpen() {
    var menu = document.querySelector('.layer_menu');
    return !!(menu && menu.children.length && getComputedStyle(menu).display !== 'none');
  }
  function clear() { tracks.clear(); show(''); }
  function attachHowl(howl) {
    if (howl.__zhAttached) return;
    howl.__zhAttached = true;
    var source = Array.isArray(howl._src) ? howl._src[0] : howl._src;
    var cues = cuesFor(source);
    if (!cues || !cues.length) return;
    var ids = new Map();
    function start(id) {
      var key = ids.get(id) || {};
      ids.set(id, key);
      tracks.set(key, { sequence: ++sequence, cues: cues, read: function () {
        if (howl.state() === 'unloaded') { tracks.delete(key); return NaN; }
        return Number(howl.seek(undefined, id));
      }});
    }
    function stop(id) {
      if (typeof id === 'undefined') { ids.forEach(function (key) { tracks.delete(key); }); ids.clear(); }
      else { tracks.delete(ids.get(id)); ids.delete(id); }
    }
    howl.on('play', start);
    howl.on('stop', stop);
    howl.on('end', function (id) { if (!howl.loop(id)) stop(id); });
    // This shipped Howler version has no unload event; intercept its method.
    var originalUnload = howl.unload;
    howl.unload = function () { stop(); return originalUnload.apply(this, arguments); };
    (howl._sounds || []).forEach(function (sound) { if (howl.playing(sound._id)) start(sound._id); });
  }
  function attachVideo(video) {
    if (video.__zhAttached) return;
    video.__zhAttached = true;
    function start() {
      var cues = cuesFor(video.currentSrc || video.src);
      if (!cues || !cues.length || video.muted || video.volume === 0) { tracks.delete(video); return; }
      tracks.set(video, { sequence: ++sequence, cues: cues, read: function () {
        if (!video.isConnected || video.ended || video.muted || video.volume === 0) { tracks.delete(video); return NaN; }
        return video.currentTime;
      }});
    }
    video.addEventListener('playing', start);
    video.addEventListener('loadedmetadata', function () { if (!video.paused) start(); });
    video.addEventListener('ended', function () { if (!video.loop) tracks.delete(video); });
    video.addEventListener('emptied', function () { tracks.delete(video); });
    if (!video.paused) start();
  }
  function scan(node) {
    if (node.nodeType !== 1) return;
    if (node.tagName === 'VIDEO') attachVideo(node);
    node.querySelectorAll('video').forEach(attachVideo);
  }
  function install() {
    if (!window.Howl || !document.body) return false;
    var originalInit = Howl.prototype.init;
    Howl.prototype.init = function () {
      var result = originalInit.apply(this, arguments);
      attachHowl(this);
      return result;
    };
    (Howler._howls || []).forEach(attachHowl);
    scan(document.body);
    new MutationObserver(function (changes) {
      changes.forEach(function (change) { change.addedNodes.forEach(scan); });
    }).observe(document.body, { childList: true, subtree: true });
    var tags = window.tyrano && tyrano.plugin && tyrano.plugin.kag && tyrano.plugin.kag.tag;
    if (tags && tags.jump && tags.jump.start) {
      var jump = tags.jump.start;
      tags.jump.start = function () { clear(); return jump.apply(this, arguments); };
    }
    var flow = window.tyrano && tyrano.plugin && tyrano.plugin.kag && tyrano.plugin.kag.ftag;
    if (flow && flow.nextOrderWithLabel) {
      var nextLabel = flow.nextOrderWithLabel;
      flow.nextOrderWithLabel = function () { clear(); return nextLabel.apply(this, arguments); };
    }
    // Save/load replaces the layer tree; discard any caption from the previous state.
    var menuPrototype = window.tyrano && tyrano.plugin && tyrano.plugin.kag && tyrano.plugin.kag.menu;
    if (menuPrototype && menuPrototype.loadGameData) {
      var load = menuPrototype.loadGameData;
      menuPrototype.loadGameData = function () { clear(); return load.apply(this, arguments); };
    }
    setInterval(function () {
      if (!enabled || menuOpen()) { show(''); return; }
      var best = -1, text = '';
      tracks.forEach(function (track) {
        var time;
        try { time = track.read(); } catch (_) { time = NaN; }
        var candidate = cueAt(track.cues, time);
        if (candidate && track.sequence > best) { best = track.sequence; text = candidate; }
      });
      show(text);
    }, 40);
    return true;
  }
  window.GameZhSubtitles = {
    version: '1.0.0', clear: clear, normalize: normalize, cueAt: cueAt,
    setEnabled: function (value) { enabled = !!value; if (!enabled) show(''); },
    status: function () { return { enabled: enabled, activeMedia: tracks.size, mediaCount: Object.keys(database.media).length, text: lastText }; }
  };
  if (!install()) {
    var timer = setInterval(function () { if (install()) clearInterval(timer); }, 100);
  }
})();
