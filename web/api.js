/**
 * RadioApp Web Control Room - API & WebSocket Bridge
 * Handles real-time communication with the C# RadioApp daemon.
 * Queries live SQLite database, real Windows filesystem drives & directories.
 */

class RadioApiClient {
  constructor() {
    this.ws = null;
    this.isConnected = false;
    this.eventListeners = new Map();
    this.reconnectTimer = null;
    
    this.baseUrl = window.location.origin;
    this.wsUrl = (window.location.protocol === 'https:' ? 'wss://' : 'ws://') + window.location.host + '/ws';
  }

  init() {
    this.fetchInitialState();
    this.connectWebSocket();
  }

  async fetchInitialState() {
    try {
      const res = await fetch('/api/state');
      if (res.ok) {
        const payload = await res.json();
        this.emit('stateSync', payload.data || payload);
      }
    } catch (e) {
      console.warn('[RadioApp API] Initial HTTP state fetch failed. Awaiting WebSocket...');
    }
  }

  async fetchDrives() {
    try {
      const res = await fetch('/api/drives');
      if (res.ok) {
        const json = await res.json();
        return json.drives || [];
      }
    } catch (e) {
      console.error('[RadioApp API] Failed to fetch drives:', e);
    }
    return [];
  }

  async browseDirectory(path) {
    try {
      const url = `/api/browse?path=${encodeURIComponent(path || '')}`;
      const res = await fetch(url);
      if (res.ok) {
        return await res.json();
      }
    } catch (e) {
      console.error('[RadioApp API] Failed to browse directory:', e);
    }
    return { success: false, message: 'Server unreachable' };
  }

  async saveSettings(settingsObj) {
    try {
      const res = await fetch('/api/settings/update', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(settingsObj)
      });
      return res.ok;
    } catch (e) {
      console.error('[RadioApp API] Failed to save settings:', e);
      return false;
    }
  }

  async addWatchedFolder(folder) {
    try {
      const res = await fetch('/api/folders/add', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ folder })
      });
      return res.ok;
    } catch (e) {
      console.error('[RadioApp API] Failed to add watched folder:', e);
      return false;
    }
  }

  async removeWatchedFolder(folder) {
    try {
      const res = await fetch('/api/folders/remove', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ folder })
      });
      return res.ok;
    } catch (e) {
      console.error('[RadioApp API] Failed to remove watched folder:', e);
      return false;
    }
  }

  on(event, callback) {
    if (!this.eventListeners.has(event)) {
      this.eventListeners.set(event, []);
    }
    this.eventListeners.get(event).push(callback);
  }

  emit(event, data) {
    if (this.eventListeners.has(event)) {
      this.eventListeners.get(event).forEach(cb => cb(data));
    }
  }

  connectWebSocket() {
    try {
      this.ws = new WebSocket(this.wsUrl);

      this.ws.onopen = () => {
        console.log('[RadioApp WS] Connected to live C# broadcast engine.');
        this.isConnected = true;
        this.emit('connectionChange', { connected: true, live: true });
      };

      this.ws.onmessage = (event) => {
        try {
          const payload = JSON.parse(event.data);
          this.handleServerMessage(payload);
        } catch (e) {
          console.error('[RadioApp WS] Message parse error:', e);
        }
      };

      this.ws.onclose = () => {
        this.isConnected = false;
        this.emit('connectionChange', { connected: false, live: false });
        this.scheduleReconnect();
      };

      this.ws.onerror = () => {
        this.emit('connectionChange', { connected: false, live: false });
      };
    } catch (err) {
      this.scheduleReconnect();
    }
  }

  scheduleReconnect() {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = setTimeout(() => {
      this.connectWebSocket();
    }, 4000);
  }

  handleServerMessage(msg) {
    if (msg.type === 'STATE_SYNC') {
      this.emit('stateSync', msg.data);
    }
  }

  sendCommand(action, payload = {}) {
    const data = JSON.stringify({ action, ...payload });
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(data);
    }
  }

  deckPlay(deckId) {
    this.sendCommand('PLAY', { deck: deckId });
    try { fetch('/api/player/play', { method: 'POST' }); } catch {}
  }
  deckPause(deckId) {
    this.sendCommand('PAUSE', { deck: deckId });
    try { fetch('/api/player/pause', { method: 'POST' }); } catch {}
  }
  deckStop(deckId) {
    this.sendCommand('STOP', { deck: deckId });
    try { fetch('/api/player/pause', { method: 'POST' }); } catch {}
  }
  deckSkip(deckId) {
    this.sendCommand('SKIP', { deck: deckId });
    try { fetch('/api/player/skip', { method: 'POST' }); } catch {}
  }
  deckCue(deckId) { this.sendCommand('CUE', { deck: deckId }); }
  toggleStream() { this.sendCommand('TOGGLE_STREAM'); }
  startStream() {
    this.sendCommand('CONNECT_STREAM');
    try { fetch('/api/stream/connect', { method: 'POST' }); } catch {}
  }
  stopStream() {
    this.sendCommand('DISCONNECT_STREAM');
    try { fetch('/api/stream/disconnect', { method: 'POST' }); } catch {}
  }
  clearQueue() { this.sendCommand('CLEAR_QUEUE'); }
  rescanLibrary() { this.sendCommand('RESCAN_LIBRARY'); }
  setVolume(deckId, val) { this.sendCommand('SET_VOLUME', { deck: deckId, volume: val }); }
  toggleMic(active) { this.sendCommand('TOGGLE_MIC', { active }); }
}

window.radioApi = new RadioApiClient();
