/**
 * RadioApp Web Control Room - API & WebSocket Bridge
 * Handles real-time communication with the C# RadioApp daemon.
 * Falls back seamlessly to interactive simulator mode when offline.
 */

class RadioApiClient {
  constructor() {
    this.ws = null;
    this.isConnected = false;
    this.isSimulator = true;
    this.eventListeners = new Map();
    this.reconnectTimer = null;
    
    // Auto-detect base URL (support relative host for reverse proxy / domain)
    this.baseUrl = window.location.origin;
    this.wsUrl = (window.location.protocol === 'https:' ? 'wss://' : 'ws://') + window.location.host + '/ws';
  }

  init() {
    this.connectWebSocket();
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
        this.isSimulator = false;
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
        if (this.isConnected) {
          console.warn('[RadioApp WS] Disconnected. Falling back to local simulator.');
        }
        this.isConnected = false;
        this.isSimulator = true;
        this.emit('connectionChange', { connected: false, live: false });
        this.scheduleReconnect();
      };

      this.ws.onerror = () => {
        this.isSimulator = true;
        this.emit('connectionChange', { connected: false, live: false });
      };
    } catch (err) {
      this.isSimulator = true;
      this.scheduleReconnect();
    }
  }

  scheduleReconnect() {
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.reconnectTimer = setTimeout(() => {
      this.connectWebSocket();
    }, 5000);
  }

  handleServerMessage(msg) {
    switch (msg.type) {
      case 'STATE_SYNC':
        this.emit('stateSync', msg.data);
        break;
      case 'VU_LEVELS':
        this.emit('vuUpdate', msg.data);
        break;
      case 'TRACK_CHANGED':
        this.emit('trackChanged', msg.data);
        break;
      case 'QUEUE_UPDATED':
        this.emit('queueUpdated', msg.data);
        break;
      case 'STREAM_STATUS':
        this.emit('streamStatus', msg.data);
        break;
      default:
        this.emit('message', msg);
    }
  }

  async sendCommand(action, params = {}) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify({ action, ...params }));
      return { success: true };
    }

    // REST fallback
    try {
      const res = await fetch(`${this.baseUrl}/api/control`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action, ...params })
      });
      return await res.json();
    } catch (e) {
      // In simulation mode, return mock success
      return { success: true, simulated: true };
    }
  }

  // High-level helper methods
  deckPlay(deckId) { return this.sendCommand('DECK_PLAY', { deck: deckId }); }
  deckPause(deckId) { return this.sendCommand('DECK_PAUSE', { deck: deckId }); }
  deckStop(deckId) { return this.sendCommand('DECK_STOP', { deck: deckId }); }
  deckCue(deckId) { return this.sendCommand('DECK_CUE', { deck: deckId }); }
  deckLoad(deckId, trackId) { return this.sendCommand('DECK_LOAD', { deck: deckId, trackId }); }
  setVolume(deckId, volume) { return this.sendCommand('SET_VOLUME', { deck: deckId, volume }); }
  setPitch(deckId, pitch) { return this.sendCommand('SET_PITCH', { deck: deckId, pitch }); }
  setCrossfade(value) { return this.sendCommand('SET_CROSSFADE', { value }); }
  toggleStream() { return this.sendCommand('TOGGLE_STREAM'); }
  toggleMic(active) { return this.sendCommand('TOGGLE_MIC', { active }); }
}

window.radioApi = new RadioApiClient();
