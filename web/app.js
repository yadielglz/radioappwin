/**
 * RadioApp Studio - Single Deck Broadcast & Rule Engine Logic
 * Handles real-time synchronization from the C# RadioApp backend.
 */

document.addEventListener('DOMContentLoaded', () => {
  // Application State
  const state = {
    isBroadcasting: false,
    autoDj: true,
    autoDjFillCurrentHour: true,
    micDucking: false,
    listeners: 0,
    peakListeners: 0,
    uptimeSeconds: 0,
    
    // Normalization & DSP
    normalization: {
      enabled: true,
      targetDb: -3.0,
      maxGainDb: 9.0,
      limiterEnabled: true
    },

    // Gapless Settings
    gapless: {
      enabled: true,
      silenceThresholdDb: -42.0,
      silenceHoldMs: 650,
      scanWindowSec: 20.0
    },

    // Sweeper / Jingle Rule State
    sweeperRules: {
      enabled: true,
      folder: 'D:\\Radio\\Sweepers',
      minSongs: 3,
      maxSongs: 6,
      songsSinceLast: 1,
      targetInterval: 4
    },

    // Radio ID Rule State
    radioIdRules: {
      enabled: true,
      folder: 'D:\\Radio\\RadioIDs',
      targetMinute: 0,
      toleranceBefore: 5,
      toleranceAfter: 10,
      hasTriggeredThisHour: false
    },

    // Watched Library Folders
    watchedFolders: [],

    // Hourly Tag Rotation Rules
    scheduleRules: [],

    autoDjDefaultTag: 'Salsa',
    autoDjQueueDepth: 5,

    // On-Air Track
    currentTrack: {
      id: 0,
      type: 'song',
      title: 'Standby / Loading...',
      artist: 'RadioApp 5.0',
      album: 'Station Output',
      category: 'General',
      duration: 180,
      position: 0,
      cueIn: 0.0,
      cueOut: 180.0,
      state: 'STOPPED',
      waveformPeaks: generateMockWaveform(200)
    },

    // Live Broadcast Queue (from C#)
    playlist: [],

    // Media Library Database (from SQLite radioapp.db)
    library: [],

    // Sweepers & Radio IDs
    sweepers: [],

    // Virtual Server File System for Directory Browser
    serverFileSystem: {
      'D:\\': ['Radio', 'Music', 'Backups', 'Recordings'],
      'D:\\Radio': ['Sweepers', 'RadioIDs', 'VoiceTracks', 'Promos'],
      'D:\\Radio\\Sweepers': ['MorningSweeps', 'WeekendSweeps', 'Stingers'],
      'D:\\Radio\\RadioIDs': ['LegalIDs', 'TopHourIDs', 'QuickIDs'],
      'D:\\Music': ['MainLibrary', 'NewRotations', 'PuertoRicoSalsa', 'RockEnEspanol', 'UrbanReggaeton'],
      'C:\\': ['Users', 'Program Files', 'RadioApp', 'AppData'],
      'C:\\RadioApp': ['Database', 'Cache', 'Logs'],
      'Z:\\': ['Music', 'Radio', 'SharedMedia', 'StudioArchive', 'MasterFLAC']
    },

    browserState: {
      targetType: 'sweeper',
      currentDrive: 'D:\\',
      currentPath: 'D:\\Radio\\Sweepers',
      selectedPath: 'D:\\Radio\\Sweepers'
    },

    editingTrackId: null,
    editingRuleId: null
  };

  // DOM Elements
  const els = {
    streamPill: document.getElementById('streamStatusPill'),
    streamStatusText: document.getElementById('streamStatusText'),
    onAirTitle: document.getElementById('onAirTitle'),
    onAirTypeTag: document.getElementById('onAirTypeTag'),
    btnToggleBroadcast: document.getElementById('btnToggleBroadcast'),
    btnBroadcastText: document.getElementById('btnBroadcastText'),
    listenerCount: document.getElementById('listenerCount'),
    streamUptime: document.getElementById('streamUptime'),
    playerState: document.getElementById('playerState'),
    timeElapsed: document.getElementById('timeElapsed'),
    timeTotal: document.getElementById('timeTotal'),
    timeRemaining: document.getElementById('timeRemaining'),
    trackTitle: document.getElementById('trackTitle'),
    trackMeta: document.getElementById('trackMeta'),
    heroIcon: document.getElementById('heroIcon'),
    trackTagBadge: document.getElementById('trackTagBadge'),
    nextUpPreview: document.getElementById('nextUpPreview'),
    progressBar: document.getElementById('progressBar'),
    waveformCanvas: document.getElementById('waveformCanvas'),
    btnPlay: document.getElementById('btnPlay'),
    btnPause: document.getElementById('btnPause'),
    btnStop: document.getElementById('btnStop'),
    btnCue: document.getElementById('btnCue'),
    btnSkip: document.getElementById('btnSkip'),
    playerVolume: document.getElementById('playerVolume'),
    playerVolumeVal: document.getElementById('playerVolumeVal'),
    vuLeft: document.getElementById('vuLeft'),
    vuRight: document.getElementById('vuRight'),
    btnMicDucking: document.getElementById('btnMicDucking'),
    hourCoverageText: document.getElementById('hourCoverageText'),
    projectedIdInfo: document.getElementById('projectedIdInfo'),
    hourProgressFill: document.getElementById('hourProgressFill'),
    playlistTableBody: document.getElementById('playlistTableBody'),
    playlistCount: document.getElementById('playlistCount'),
    libraryTableBody: document.getElementById('libraryTableBody'),
    libraryCount: document.getElementById('libraryCount'),
    librarySearchInput: document.getElementById('librarySearchInput'),
    categoryFilterSelect: document.getElementById('categoryFilterSelect'),
    librarySyncStatusText: document.getElementById('librarySyncStatusText'),
    watchedFoldersList: document.getElementById('watchedFoldersList'),
    btnAddWatchedFolder: document.getElementById('btnAddWatchedFolder'),
    btnBrowseAddMusicFolder: document.getElementById('btnBrowseAddMusicFolder'),
    txtSweeperFolderPath: document.getElementById('txtSweeperFolderPath'),
    txtRadioIdFolderPath: document.getElementById('txtRadioIdFolderPath'),
    btnUpdateSweeperFolder: document.getElementById('btnUpdateSweeperFolder'),
    btnUpdateRadioIdFolder: document.getElementById('btnUpdateRadioIdFolder'),
    btnBrowseSweeperFolder: document.getElementById('btnBrowseSweeperFolder'),
    btnBrowseRadioIdFolder: document.getElementById('btnBrowseRadioIdFolder'),
    btnBrowseSweeperFolderMain: document.getElementById('btnBrowseSweeperFolderMain'),
    btnBrowseRadioIdFolderMain: document.getElementById('btnBrowseRadioIdFolderMain'),
    btnBrowseSweeperFolderSettings: document.getElementById('btnBrowseSweeperFolderSettings'),
    btnBrowseRadioIdFolderSettings: document.getElementById('btnBrowseRadioIdFolderSettings'),
    btnOpenFolderManager: document.getElementById('btnOpenFolderManager'),
    lblSweeperFolder: document.getElementById('lblSweeperFolder'),
    lblRadioIdFolder: document.getElementById('lblRadioIdFolder'),
    sweeperRuleText: document.getElementById('sweeperRuleText'),
    sweeperCountdown: document.getElementById('sweeperCountdown'),
    radioIdRuleText: document.getElementById('radioIdRuleText'),
    radioIdWindowStatus: document.getElementById('radioIdWindowStatus'),
    badgeSweeperState: document.getElementById('badgeSweeperState'),
    badgeRadioIdState: document.getElementById('badgeRadioIdState'),
    activeRotationTimeText: document.getElementById('activeRotationTimeText'),
    lblActiveRotationTag: document.getElementById('lblActiveRotationTag'),
    activeRotationDesc: document.getElementById('activeRotationDesc'),
    btnViewRotationRules: document.getElementById('btnViewRotationRules'),
    calloutRuleTitle: document.getElementById('calloutRuleTitle'),
    calloutRuleDetails: document.getElementById('calloutRuleDetails'),
    btnTriggerRotationFill: document.getElementById('btnTriggerRotationFill'),
    btnInsertSweeper: document.getElementById('btnInsertSweeper'),
    btnInsertRadioId: document.getElementById('btnInsertRadioId'),
    btnToggleAutoDj: document.getElementById('btnToggleAutoDj'),
    btnToggleSweepers: document.getElementById('btnToggleSweepers'),
    btnToggleRadioIds: document.getElementById('btnToggleRadioIds'),
    btnClearPlaylist: document.getElementById('btnClearPlaylist'),
    btnRandomizePlaylist: document.getElementById('btnRandomizePlaylist'),
    btnAutoFillPlaylist: document.getElementById('btnAutoFillPlaylist'),
    btnImportTrack: document.getElementById('btnImportTrack'),
    btnImportFolder: document.getElementById('btnImportFolder'),
    btnRescanLibrary: document.getElementById('btnRescanLibrary'),
    sweepersGrid: document.getElementById('sweepersGrid'),
    scheduleRulesList: document.getElementById('scheduleRulesList'),
    btnAddScheduleRule: document.getElementById('btnAddScheduleRule'),
    btnOpenSettings: document.getElementById('btnOpenSettings'),
    btnCloseSettingsModal: document.getElementById('btnCloseSettingsModal'),
    btnCancelSettings: document.getElementById('btnCancelSettings'),
    btnSaveSettings: document.getElementById('btnSaveSettings'),
    settingsModal: document.getElementById('settingsModal'),
    trackEditModal: document.getElementById('trackEditModal'),
    btnCloseTrackModal: document.getElementById('btnCloseTrackModal'),
    btnCancelTrackEdit: document.getElementById('btnCancelTrackEdit'),
    btnSaveTrackEdit: document.getElementById('btnSaveTrackEdit'),
    editTrackTitle: document.getElementById('editTrackTitle'),
    editTrackArtist: document.getElementById('editTrackArtist'),
    editTrackAlbum: document.getElementById('editTrackAlbum'),
    editTrackCategory: document.getElementById('editTrackCategory'),
    editTrackYear: document.getElementById('editTrackYear'),
    editTrackCueIn: document.getElementById('editTrackCueIn'),
    editTrackCueOut: document.getElementById('editTrackCueOut'),
    
    // Rotation Rule Modal
    rotationRuleModal: document.getElementById('rotationRuleModal'),
    ruleModalTitle: document.getElementById('ruleModalTitle'),
    btnCloseRuleModal: document.getElementById('btnCloseRuleModal'),
    btnCancelRuleEdit: document.getElementById('btnCancelRuleEdit'),
    btnSaveRuleEdit: document.getElementById('btnSaveRuleEdit'),
    editRuleTitle: document.getElementById('editRuleTitle'),
    editRuleStartTime: document.getElementById('editRuleStartTime'),
    editRuleEndTime: document.getElementById('editRuleEndTime'),
    editRuleTag: document.getElementById('editRuleTag'),
    editRuleDesc: document.getElementById('editRuleDesc'),

    // Folder Browser Modal
    folderBrowserModal: document.getElementById('folderBrowserModal'),
    browserModalTitle: document.getElementById('browserModalTitle'),
    btnCloseBrowserModal: document.getElementById('btnCloseBrowserModal'),
    btnCancelBrowser: document.getElementById('btnCancelBrowser'),
    btnConfirmSelectFolder: document.getElementById('btnConfirmSelectFolder'),
    driveButtons: document.getElementById('driveButtons'),
    breadcrumbBar: document.getElementById('breadcrumbBar'),
    directoryList: document.getElementById('directoryList'),
    txtSelectedBrowserPath: document.getElementById('txtSelectedBrowserPath'),
    realtimeLoudness: document.getElementById('realtimeLoudness')
  };

  // Init
  initUI();
  initWaveform();
  initAudioLoop();
  initTabs();
  initMobileNav();
  initFolderBrowser();
  initWebSocketSync();

  // ----------------------------------------------------
  // Live Backend State Sync (Hydrates Real DB & Settings)
  // ----------------------------------------------------
  function initWebSocketSync() {
    window.radioApi.on('stateSync', (data) => {
      if (!data) return;
      console.log('[RadioApp Web] Applying live state sync from C#:', data);

      // Stream & Activity Status
      state.isBroadcasting = !!data.isLive;
      if (data.streamStatus) {
        els.streamStatusText.textContent = data.streamStatus.toUpperCase();
      }

      // Live Deck A
      if (data.deckA) {
        state.currentTrack.title = data.deckA.title || 'No track loaded';
        state.currentTrack.artist = data.deckA.artist || '';
        state.currentTrack.album = data.deckA.album || '';
        state.currentTrack.state = data.deckA.state || 'STOPPED';
        state.currentTrack.position = data.deckA.position || 0;
        state.currentTrack.duration = data.deckA.duration > 0 ? data.deckA.duration : 180;
        renderCurrentTrack();
      }

      // Live Broadcast Queue
      if (Array.isArray(data.queue)) {
        state.playlist = data.queue.map(q => ({
          id: q.id,
          type: q.type || 'song',
          title: q.title,
          artist: q.artist,
          album: q.album,
          duration: q.duration,
          category: q.category || 'Music'
        }));
        renderPlaylist();
      }

      // Live SQLite Media Library (radioapp.db)
      if (Array.isArray(data.library) && data.library.length > 0) {
        state.library = data.library.map(item => ({
          id: item.id,
          type: 'song',
          title: item.title,
          artist: item.artist,
          album: item.album || 'Single',
          duration: item.duration || '03:30',
          category: item.category || 'General',
          year: item.year || 2026,
          cueIn: item.cueIn || 0.0,
          cueOut: item.cueOut || 0.0
        }));
        renderLibrary(state.library);
        els.libraryCount.textContent = state.library.length;
        els.librarySyncStatusText.textContent = `Library Database: ${state.library.length} tracks loaded from radioapp.db`;
      }

      // Watched Folders
      if (Array.isArray(data.watchedFolders)) {
        state.watchedFolders = data.watchedFolders;
        renderWatchedFolders();
      }

      // Settings (Jingles, Radio IDs, Hourly Rotation)
      if (data.settings) {
        const s = data.settings;
        if (s.sweeperFolder) state.sweeperRules.folder = s.sweeperFolder;
        if (s.radioIdFolder) state.radioIdRules.folder = s.radioIdFolder;
        if (s.sweeperMin) state.sweeperRules.minSongs = s.sweeperMin;
        if (s.sweeperMax) state.sweeperRules.maxSongs = s.sweeperMax;
        if (s.sweepersEnabled !== undefined) state.sweeperRules.enabled = s.sweepersEnabled;

        if (s.radioIdMinute !== undefined) state.radioIdRules.targetMinute = s.radioIdMinute;
        if (s.toleranceBefore !== undefined) state.radioIdRules.toleranceBefore = s.toleranceBefore;
        if (s.toleranceAfter !== undefined) state.radioIdRules.toleranceAfter = s.toleranceAfter;
        if (s.radioIdsEnabled !== undefined) state.radioIdRules.enabled = s.radioIdsEnabled;

        if (s.autoDjDefaultTag) state.autoDjDefaultTag = s.autoDjDefaultTag;
        if (s.autoDjFillCurrentHour !== undefined) state.autoDjFillCurrentHour = s.autoDjFillCurrentHour;

        if (s.hourlyTagRules) {
          parseRawRulesString(s.hourlyTagRules);
          const rawTextarea = document.getElementById('cfgHourlyTagRulesRaw');
          if (rawTextarea) rawTextarea.value = s.hourlyTagRules;
        }

        updateRuleUI();
        updateRotationStatus();
      }

      // Stream Server Details (SHOUTcast / Icecast)
      if (data.streamConfig) {
        const sc = data.streamConfig;
        const sType = document.getElementById('cfgStreamServerType');
        if (sType && sc.serverType) sType.value = sc.serverType.toLowerCase().includes('icecast') ? 'Icecast' : 'Shoutcast';

        const sHost = document.getElementById('cfgStreamHost');
        if (sHost && sc.host !== undefined) sHost.value = sc.host;

        const sPort = document.getElementById('cfgStreamPort');
        if (sPort && sc.port !== undefined) sPort.value = sc.port;

        const sMount = document.getElementById('cfgStreamMount');
        if (sMount && sc.mount !== undefined) sMount.value = sc.mount;

        const sUser = document.getElementById('cfgStreamUsername');
        if (sUser && sc.username !== undefined) sUser.value = sc.username;

        const sPass = document.getElementById('cfgStreamPassword');
        if (sPass && sc.password !== undefined) sPass.value = sc.password;

        const sBitrate = document.getElementById('cfgStreamBitrate');
        if (sBitrate && sc.bitrate !== undefined) sBitrate.value = sc.bitrate.toString();

        const sId = document.getElementById('cfgStreamShoutcastId');
        if (sId && sc.shoutcastStreamId !== undefined) sId.value = sc.shoutcastStreamId;

        const sAuto = document.getElementById('cfgAutoConnectStream');
        if (sAuto && sc.autoConnect !== undefined) sAuto.value = sc.autoConnect ? 'true' : 'false';
      }

      // Categories
      if (Array.isArray(data.categories) && data.categories.length > 0) {
        els.categoryFilterSelect.innerHTML = '<option value="ALL">All Categories</option>';
        data.categories.forEach(cat => {
          const opt = document.createElement('option');
          opt.value = cat;
          opt.textContent = cat;
          els.categoryFilterSelect.appendChild(opt);
        });
      }

      updateHourCoverage();
      updateHeader();
    });

    window.radioApi.init();
  }

  // ----------------------------------------------------
  // UI Initialization
  // ----------------------------------------------------
  function initUI() {
    renderCurrentTrack();
    renderPlaylist();
    renderLibrary(state.library);
    renderWatchedFolders();
    renderSweepers();
    renderScheduleRules();
    updateRotationStatus();
    updateHourCoverage();
    updateRuleUI();

    // Transport Controls -> Emit to C# Backend
    els.btnPlay.addEventListener('click', () => {
      window.radioApi.deckPlay('A');
      state.currentTrack.state = 'PLAYING';
      els.playerState.textContent = 'PLAYING';
      updateHeader();
    });

    els.btnPause.addEventListener('click', () => {
      window.radioApi.deckPause('A');
      state.currentTrack.state = 'PAUSED';
      els.playerState.textContent = 'PAUSED';
    });

    els.btnStop.addEventListener('click', () => {
      window.radioApi.deckStop('A');
      state.currentTrack.state = 'STOPPED';
      state.currentTrack.position = 0;
      els.playerState.textContent = 'STOPPED';
      updateTrackTimers();
    });

    els.btnCue.addEventListener('click', () => {
      window.radioApi.deckCue('A');
      state.currentTrack.state = 'CUED';
      state.currentTrack.position = state.currentTrack.cueIn || 0;
      els.playerState.textContent = 'CUED';
      updateTrackTimers();
    });

    els.btnSkip.addEventListener('click', () => {
      window.radioApi.deckSkip('A');
      skipToNext();
    });

    // Volume Slider
    els.playerVolume.addEventListener('input', (e) => {
      const val = parseFloat(e.target.value);
      els.playerVolumeVal.textContent = Math.round(val * 100) + '%';
      window.radioApi.setVolume('A', val);
    });

    // Mic Ducking
    els.btnMicDucking.addEventListener('click', () => {
      state.micDucking = !state.micDucking;
      els.btnMicDucking.classList.toggle('active', state.micDucking);
      window.radioApi.toggleMic(state.micDucking);
    });

    // Broadcast Toggle
    els.btnToggleBroadcast.addEventListener('click', () => {
      window.radioApi.toggleStream();
    });

    // Folder Manager Button in Top Header
    els.btnOpenFolderManager.addEventListener('click', () => {
      const folderTabBtn = document.getElementById('tabFoldersBtn');
      if (folderTabBtn) folderTabBtn.click();
      document.querySelector('.studio-manager-section').scrollIntoView({ behavior: 'smooth' });
    });

    // View Rotation Rules Button
    els.btnViewRotationRules.addEventListener('click', () => {
      const schedTabBtn = document.getElementById('tabScheduleBtn');
      if (schedTabBtn) schedTabBtn.click();
      document.querySelector('.studio-manager-section').scrollIntoView({ behavior: 'smooth' });
    });

    // Insertion Buttons
    els.btnInsertSweeper.addEventListener('click', () => insertSweeperNext());
    els.btnInsertRadioId.addEventListener('click', () => insertRadioIdNext());

    // Automation Toggles
    els.btnToggleAutoDj.addEventListener('click', () => {
      state.autoDj = !state.autoDj;
      els.btnToggleAutoDj.classList.toggle('active', state.autoDj);
      document.getElementById('autoDjPill').dataset.active = state.autoDj ? 'true' : 'false';
      document.getElementById('autoDjText').textContent = state.autoDj ? 'AUTO-DJ ON' : 'AUTO-DJ OFF';
    });

    els.btnToggleSweepers.addEventListener('click', () => {
      state.sweeperRules.enabled = !state.sweeperRules.enabled;
      els.btnToggleSweepers.classList.toggle('active', state.sweeperRules.enabled);
      updateRuleUI();
    });

    els.btnToggleRadioIds.addEventListener('click', () => {
      state.radioIdRules.enabled = !state.radioIdRules.enabled;
      els.btnToggleRadioIds.classList.toggle('active', state.radioIdRules.enabled);
      updateRuleUI();
    });

    // Playlist Actions
    els.btnClearPlaylist.addEventListener('click', () => {
      window.radioApi.clearQueue();
      state.playlist = [];
      renderPlaylist();
      updateHourCoverage();
    });

    els.btnRandomizePlaylist.addEventListener('click', () => {
      state.playlist.sort(() => Math.random() - 0.5);
      renderPlaylist();
      updateHourCoverage();
    });

    els.btnAutoFillPlaylist.addEventListener('click', calculateAndFillCurrentHour);
    els.btnTriggerRotationFill.addEventListener('click', calculateAndFillCurrentHour);

    // Library Filtering & Search
    els.librarySearchInput.addEventListener('input', applyLibraryFilters);
    els.categoryFilterSelect.addEventListener('change', applyLibraryFilters);

    // Folder Browse Triggers
    els.btnBrowseSweeperFolder.addEventListener('click', () => openFolderBrowser('sweeper'));
    els.btnBrowseRadioIdFolder.addEventListener('click', () => openFolderBrowser('radioid'));
    els.btnBrowseSweeperFolderMain.addEventListener('click', () => openFolderBrowser('sweeper'));
    els.btnBrowseRadioIdFolderMain.addEventListener('click', () => openFolderBrowser('radioid'));
    els.btnBrowseAddMusicFolder.addEventListener('click', () => openFolderBrowser('library'));
    els.btnBrowseSweeperFolderSettings.addEventListener('click', () => openFolderBrowser('sweeper'));
    els.btnBrowseRadioIdFolderSettings.addEventListener('click', () => openFolderBrowser('radioid'));

    els.btnAddWatchedFolder.addEventListener('click', () => openFolderBrowser('library'));

    els.btnUpdateSweeperFolder.addEventListener('click', () => {
      state.sweeperRules.folder = els.txtSweeperFolderPath.value;
      updateRuleUI();
      alert(`Sweeper folder updated to: ${state.sweeperRules.folder}`);
    });

    els.btnUpdateRadioIdFolder.addEventListener('click', () => {
      state.radioIdRules.folder = els.txtRadioIdFolderPath.value;
      updateRuleUI();
      alert(`Radio ID folder updated to: ${state.radioIdRules.folder}`);
    });

    // Rotation Rule Modal
    els.btnAddScheduleRule.addEventListener('click', () => openRuleEdit(null));
    els.btnCloseRuleModal.addEventListener('click', () => els.rotationRuleModal.classList.remove('open'));
    els.btnCancelRuleEdit.addEventListener('click', () => els.rotationRuleModal.classList.remove('open'));
    els.btnSaveRuleEdit.addEventListener('click', saveRuleEdit);

    // Rescan Library -> Triggers C# SQLite scan
    els.btnRescanLibrary.addEventListener('click', () => {
      window.radioApi.rescanLibrary();
      els.librarySyncStatusText.textContent = 'Scanning watched directories for audio tags...';
    });

    // Track Metadata Modal
    els.btnCloseTrackModal.addEventListener('click', () => els.trackEditModal.classList.remove('open'));
    els.btnCancelTrackEdit.addEventListener('click', () => els.trackEditModal.classList.remove('open'));
    els.btnSaveTrackEdit.addEventListener('click', saveTrackEdit);

    // Settings Modal
    els.btnOpenSettings.addEventListener('click', () => els.settingsModal.classList.add('open'));
    els.btnCloseSettingsModal.addEventListener('click', () => els.settingsModal.classList.remove('open'));
    els.btnCancelSettings.addEventListener('click', () => els.settingsModal.classList.remove('open'));
    els.btnSaveSettings.addEventListener('click', saveSettings);
  }

  // ----------------------------------------------------
  // Hour-Filling Calculation & Coverage
  // ----------------------------------------------------
  function calculateAndFillCurrentHour() {
    const activeRule = getActiveRotationRule();
    const targetTag = activeRule.tag.toLowerCase();

    let candidates = state.library.filter(t => 
      (t.category && t.category.toLowerCase() === targetTag) ||
      (t.album && t.album.toLowerCase().includes(targetTag))
    );
    if (candidates.length === 0) candidates = state.library;

    const now = new Date();
    const secondsPassedInHour = now.getMinutes() * 60 + now.getSeconds();
    const remainingInHour = 3600 - secondsPassedInHour;

    let currentCoverage = Math.max(0, state.currentTrack.duration - state.currentTrack.position);
    state.playlist.forEach(item => {
      currentCoverage += (typeof item.duration === 'string' ? parseDuration(item.duration) : item.duration);
    });

    let songsAdded = 0;
    const shuffled = [...candidates].sort(() => Math.random() - 0.5);

    while (currentCoverage < remainingInHour - 120 && shuffled.length > 0) {
      const cand = shuffled[songsAdded % shuffled.length];
      const trackDur = parseDuration(cand.duration);

      state.playlist.push({
        id: Date.now() + Math.floor(Math.random() * 10000),
        type: 'song',
        title: cand.title,
        artist: cand.artist,
        album: cand.album,
        category: cand.category,
        duration: trackDur
      });
      currentCoverage += trackDur;
      songsAdded++;

      if (songsAdded % 4 === 0 && state.sweeperRules.enabled) {
        const sw = state.sweepers[Math.floor(Math.random() * (state.sweepers.length || 1))] || { title: 'Station Jingle', duration: 8 };
        state.playlist.push({
          id: Date.now() + Math.floor(Math.random() * 10000),
          type: 'sweeper',
          title: sw.title,
          artist: 'Station Branding',
          album: 'Sweepers',
          category: 'Jingle',
          duration: sw.duration
        });
        currentCoverage += sw.duration;
      }
    }

    if (state.radioIdRules.enabled && !state.playlist.some(p => p.type === 'radioid')) {
      state.playlist.push({
        id: Date.now() + 9999,
        type: 'radioid',
        title: 'Top of the Hour Station ID (Legal)',
        artist: 'Station ID',
        album: 'Station IDs',
        category: 'StationID',
        duration: 15
      });
    }

    renderPlaylist();
    updateHourCoverage();
    updateNextUpPreview();
  }

  function updateHourCoverage() {
    const now = new Date();
    const min = now.getMinutes();
    const sec = now.getSeconds();
    const currentHourSeconds = min * 60 + sec;

    let totalQueueSec = Math.max(0, state.currentTrack.duration - state.currentTrack.position);
    state.playlist.forEach(item => {
      totalQueueSec += (typeof item.duration === 'string' ? parseDuration(item.duration) : item.duration);
    });

    const projectedTotalSec = currentHourSeconds + totalQueueSec;
    const pct = Math.min(Math.round((projectedTotalSec / 3600) * 100), 100);

    els.hourCoverageText.textContent = `${formatSeconds(Math.min(projectedTotalSec, 3600))} / 60:00`;
    els.hourProgressFill.style.width = pct + '%';

    const idItemIndex = state.playlist.findIndex(p => p.type === 'radioid');
    if (idItemIndex !== -1) {
      let secUntilId = Math.max(0, state.currentTrack.duration - state.currentTrack.position);
      for (let i = 0; i < idItemIndex; i++) {
        secUntilId += (typeof state.playlist[i].duration === 'string' ? parseDuration(state.playlist[i].duration) : state.playlist[i].duration);
      }
      const projectedIdDate = new Date(now.getTime() + secUntilId * 1000);
      const idTimeStr = projectedIdDate.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
      const idMin = projectedIdDate.getMinutes();
      const withinTol = (idMin >= 55 || idMin <= 10);

      els.projectedIdInfo.innerHTML = `Projected Radio ID Trigger: <span style="color:${withinTol ? '#10b981' : '#f59e0b'};font-weight:700;">${idTimeStr}</span> (${withinTol ? 'Within ±5m tolerance' : 'Adjusting queue'})`;
    } else {
      els.projectedIdInfo.textContent = 'Projected Radio ID: AutoDJ calculating hour boundary...';
    }
  }

  // ----------------------------------------------------
  // Active Hourly Rotation Engine
  // ----------------------------------------------------
  function getActiveRotationRule() {
    const now = new Date();
    const currentMinutes = now.getHours() * 60 + now.getMinutes();

    for (const rule of state.scheduleRules) {
      const [sh, sm] = rule.startTime.split(':').map(Number);
      const [eh, em] = rule.endTime.split(':').map(Number);
      const startMin = sh * 60 + (sm || 0);
      const endMin = eh * 60 + (em || 0);

      if (endMin > startMin) {
        if (currentMinutes >= startMin && currentMinutes < endMin) return rule;
      } else {
        if (currentMinutes >= startMin || currentMinutes < endMin) return rule;
      }
    }
    return {
      title: 'Default Rotation',
      startTime: '00:00',
      endTime: '24:00',
      tag: state.autoDjDefaultTag || 'Salsa',
      desc: 'Default station rotation.'
    };
  }

  function updateRotationStatus() {
    const active = getActiveRotationRule();
    const now = new Date();
    const timeStr = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

    els.activeRotationTimeText.textContent = `${active.startTime} - ${active.endTime}`;
    els.lblActiveRotationTag.textContent = active.tag;
    els.activeRotationDesc.textContent = `AutoDJ rotating '${active.tag}' tracks (${active.title}).`;

    els.calloutRuleTitle.textContent = `${active.title} (${active.startTime} - ${active.endTime})`;
    els.calloutRuleDetails.textContent = `Active Tag: [${active.tag}] • Local Clock: ${timeStr}`;

    renderScheduleRules();
  }

  function renderScheduleRules() {
    els.scheduleRulesList.innerHTML = '';
    const activeRule = getActiveRotationRule();

    state.scheduleRules.forEach(rule => {
      const isCurrent = (rule.tag === activeRule.tag && rule.startTime === activeRule.startTime);
      const card = document.createElement('div');
      card.className = `schedule-rule-card ${isCurrent ? 'active-rule' : ''}`;
      card.innerHTML = `
        <div>
          <div class="schedule-time-range font-mono">${rule.startTime} - ${rule.endTime} ${isCurrent ? '<span style="color:#10b981;font-size:0.75rem;">● (CURRENTLY ACTIVE)</span>' : ''}</div>
          <div style="font-weight:700;font-size:0.95rem;margin:2px 0;">${rule.title}</div>
          <div style="font-size:0.75rem;color:var(--text-secondary);">${rule.desc || 'No description'}</div>
        </div>
        <div style="display:flex;align-items:center;gap:8px;">
          <span class="badge-tag-highlight">${rule.tag}</span>
          <button class="btn-table-action btn-edit-rule" data-id="${rule.id}">Edit</button>
          <button class="btn-remove-row btn-del-rule" data-id="${rule.id}">&times;</button>
        </div>
      `;

      card.querySelector('.btn-edit-rule').addEventListener('click', () => openRuleEdit(rule));
      card.querySelector('.btn-del-rule').addEventListener('click', () => {
        state.scheduleRules = state.scheduleRules.filter(r => r.id !== rule.id);
        updateRotationStatus();
      });

      els.scheduleRulesList.appendChild(card);
    });
  }

  function openRuleEdit(rule) {
    if (rule) {
      state.editingRuleId = rule.id;
      els.ruleModalTitle.textContent = 'Edit Hourly Rotation Rule';
      els.editRuleTitle.value = rule.title;
      els.editRuleStartTime.value = rule.startTime;
      els.editRuleEndTime.value = rule.endTime;
      els.editRuleTag.value = rule.tag;
      els.editRuleDesc.value = rule.desc || '';
    } else {
      state.editingRuleId = null;
      els.ruleModalTitle.textContent = 'Add Hourly Rotation Rule';
      els.editRuleTitle.value = '';
      els.editRuleStartTime.value = '12:00';
      els.editRuleEndTime.value = '16:00';
      els.editRuleTag.value = 'Rock';
      els.editRuleDesc.value = '';
    }
    els.rotationRuleModal.classList.add('open');
  }

  function saveRuleEdit() {
    const title = els.editRuleTitle.value.trim() || 'Custom Rotation';
    const startTime = els.editRuleStartTime.value;
    const endTime = els.editRuleEndTime.value;
    const tag = els.editRuleTag.value.trim() || 'Salsa';
    const desc = els.editRuleDesc.value.trim();

    if (state.editingRuleId) {
      const rule = state.scheduleRules.find(r => r.id === state.editingRuleId);
      if (rule) {
        rule.title = title;
        rule.startTime = startTime;
        rule.endTime = endTime;
        rule.tag = tag;
        rule.desc = desc;
      }
    } else {
      state.scheduleRules.push({
        id: Date.now(),
        title,
        startTime,
        endTime,
        tag,
        desc
      });
    }

    updateRotationStatus();
    syncRulesToString();
    els.rotationRuleModal.classList.remove('open');
  }

  function syncRulesToString() {
    const rawLines = state.scheduleRules.map(r => `${r.startTime}-${r.endTime} = ${r.tag}`);
    const rawStr = rawLines.join('; ');
    const rawTextarea = document.getElementById('cfgHourlyTagRulesRaw');
    if (rawTextarea) rawTextarea.value = rawStr;
  }

  // ----------------------------------------------------
  // Real Windows Server & Network Drive Folder Browser
  // ----------------------------------------------------
  function initFolderBrowser() {
    els.btnCloseBrowserModal.addEventListener('click', () => els.folderBrowserModal.classList.remove('open'));
    els.btnCancelBrowser.addEventListener('click', () => els.folderBrowserModal.classList.remove('open'));

    // Allow user to manually type path and hit Enter
    els.txtSelectedBrowserPath.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        state.browserState.currentPath = els.txtSelectedBrowserPath.value.trim();
        state.browserState.selectedPath = state.browserState.currentPath;
        renderBrowserDirectory();
      }
    });

    // Confirm button
    els.btnConfirmSelectFolder.addEventListener('click', async () => {
      const path = els.txtSelectedBrowserPath.value.trim() || state.browserState.selectedPath;
      if (!path) return;

      if (state.browserState.targetType === 'sweeper') {
        state.sweeperRules.folder = path;
        els.txtSweeperFolderPath.value = path;
        document.getElementById('cfgSweeperFolder').value = path;
        await window.radioApi.saveSettings({ sweeperFolder: path });
      } else if (state.browserState.targetType === 'radioid') {
        state.radioIdRules.folder = path;
        els.txtRadioIdFolderPath.value = path;
        document.getElementById('cfgRadioIdFolder').value = path;
        await window.radioApi.saveSettings({ radioIdFolder: path });
      } else if (state.browserState.targetType === 'library') {
        if (!state.watchedFolders.includes(path)) {
          state.watchedFolders.push(path);
          renderWatchedFolders();
          await window.radioApi.addWatchedFolder(path);
        }
      }
      updateRuleUI();
      els.folderBrowserModal.classList.remove('open');
    });
  }

  async function openFolderBrowser(targetType) {
    state.browserState.targetType = targetType;
    if (targetType === 'sweeper') {
      els.browserModalTitle.textContent = 'Select Jingles & Sweepers Directory';
      state.browserState.selectedPath = state.sweeperRules.folder || 'C:\\';
    } else if (targetType === 'radioid') {
      els.browserModalTitle.textContent = 'Select Radio IDs Directory';
      state.browserState.selectedPath = state.radioIdRules.folder || 'C:\\';
    } else {
      els.browserModalTitle.textContent = 'Select Music Library Folder to Watch';
      state.browserState.selectedPath = (state.watchedFolders && state.watchedFolders[0]) || 'C:\\';
    }

    state.browserState.currentPath = state.browserState.selectedPath;
    await loadRealDrives();
    await renderBrowserDirectory();
    els.folderBrowserModal.classList.add('open');
  }

  async function loadRealDrives() {
    const drives = await window.radioApi.fetchDrives();
    if (!drives || drives.length === 0) return;

    els.driveButtons.innerHTML = '';
    drives.forEach(d => {
      const btn = document.createElement('button');
      const isCur = state.browserState.currentPath.toUpperCase().startsWith(d.name.toUpperCase());
      btn.className = `btn-drive ${isCur ? 'active' : ''}`;
      btn.dataset.drive = d.name;
      const label = d.label ? ` (${d.label})` : (d.driveType === 'Network' ? ' (Network)' : '');
      btn.textContent = `${d.name}${label}`;

      btn.addEventListener('click', async () => {
        els.driveButtons.querySelectorAll('.btn-drive').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        state.browserState.currentDrive = d.name;
        state.browserState.currentPath = d.name;
        state.browserState.selectedPath = d.name;
        await renderBrowserDirectory();
      });

      els.driveButtons.appendChild(btn);
    });
  }

  async function renderBrowserDirectory() {
    const p = state.browserState.currentPath;
    els.txtSelectedBrowserPath.value = state.browserState.selectedPath || p;

    // Breadcrumbs
    els.breadcrumbBar.innerHTML = '';
    const parts = p.split('\\').filter(Boolean);
    let accum = '';
    parts.forEach((part, i) => {
      accum += (i === 0 ? part + '\\' : part + '\\');
      const span = document.createElement('span');
      span.style.cursor = 'pointer';
      span.textContent = i === 0 ? part + '\\' : part + '\\ ';
      const targetP = accum.replace(/\\$/, '');
      span.addEventListener('click', async () => {
        state.browserState.currentPath = targetP.includes('\\') ? targetP : targetP + '\\';
        state.browserState.selectedPath = state.browserState.currentPath;
        await renderBrowserDirectory();
      });
      els.breadcrumbBar.appendChild(span);
    });

    // Query real Windows VM filesystem
    els.directoryList.innerHTML = '<li style="padding:16px;text-align:center;color:var(--text-muted);">Loading real directories from Windows VM...</li>';
    const res = await window.radioApi.browseDirectory(p);

    els.directoryList.innerHTML = '';

    if (!res || !res.success) {
      els.directoryList.innerHTML = `<li style="padding:16px;color:#ef4444;font-size:0.85rem;">⚠️ ${res?.message || 'Unable to access directory.'}</li>`;
      return;
    }

    if (res.parentPath) {
      const upLi = document.createElement('li');
      upLi.className = 'dir-item';
      upLi.innerHTML = `<span>⬆️</span> <strong>.. (Up to ${res.parentPath})</strong>`;
      upLi.addEventListener('click', async () => {
        state.browserState.currentPath = res.parentPath;
        state.browserState.selectedPath = res.parentPath;
        await renderBrowserDirectory();
      });
      els.directoryList.appendChild(upLi);
    }

    if ((!res.directories || res.directories.length === 0) && (!res.audioFiles || res.audioFiles.length === 0)) {
      const emptyLi = document.createElement('li');
      emptyLi.style.padding = '12px';
      emptyLi.style.color = 'var(--text-muted)';
      emptyLi.style.fontSize = '0.8rem';
      emptyLi.textContent = '(Empty folder or no subdirectories)';
      els.directoryList.appendChild(emptyLi);
    }

    if (res.directories) {
      res.directories.forEach(dir => {
        const li = document.createElement('li');
        li.className = `dir-item ${state.browserState.selectedPath === dir.fullPath ? 'selected' : ''}`;
        li.innerHTML = `<span>📁</span> <span style="font-weight:600;">${dir.name}</span>`;

        li.addEventListener('click', () => {
          state.browserState.selectedPath = dir.fullPath;
          els.txtSelectedBrowserPath.value = dir.fullPath;
          els.directoryList.querySelectorAll('.dir-item').forEach(d => d.classList.remove('selected'));
          li.classList.add('selected');
        });

        li.addEventListener('dblclick', async () => {
          state.browserState.currentPath = dir.fullPath;
          state.browserState.selectedPath = dir.fullPath;
          await renderBrowserDirectory();
        });

        els.directoryList.appendChild(li);
      });
    }

    if (res.audioFiles && res.audioFiles.length > 0) {
      res.audioFiles.slice(0, 50).forEach(file => {
        const li = document.createElement('li');
        li.className = 'dir-item';
        li.style.opacity = '0.75';
        li.innerHTML = `<span>🎵</span> <span style="font-size:0.8rem;">${file.name}</span>`;
        els.directoryList.appendChild(li);
      });
    }
  }

  // ----------------------------------------------------
  // Rules & Status Calculations
  // ----------------------------------------------------
  function updateRuleUI() {
    const sw = state.sweeperRules;
    const rid = state.radioIdRules;

    // Sweepers
    els.sweeperRuleText.textContent = `Every ${sw.minSongs} to ${sw.maxSongs} songs`;
    els.lblSweeperFolder.textContent = `${sw.folder}`;
    els.txtSweeperFolderPath.value = sw.folder;
    els.badgeSweeperState.textContent = sw.enabled ? 'ENABLED' : 'DISABLED';
    els.badgeSweeperState.className = `rule-toggle-badge ${sw.enabled ? 'active' : ''}`;
    const rem = Math.max(0, sw.targetInterval - sw.songsSinceLast);
    els.sweeperCountdown.textContent = sw.enabled ? `Next in: ${rem} song(s)` : 'Sweepers Disabled';

    // Radio IDs
    els.radioIdRuleText.textContent = `At :${rid.targetMinute.toString().padStart(2, '0')} (-${rid.toleranceBefore}m to +${rid.toleranceAfter}m window)`;
    els.lblRadioIdFolder.textContent = `${rid.folder}`;
    els.txtRadioIdFolderPath.value = rid.folder;
    els.badgeRadioIdState.textContent = rid.enabled ? 'ENABLED' : 'DISABLED';
    els.badgeRadioIdState.className = `rule-toggle-badge ${rid.enabled ? 'active' : ''}`;

    const now = new Date();
    const min = now.getMinutes();
    const isInsideWindow = (min >= 60 - rid.toleranceBefore || min <= rid.toleranceAfter);

    if (rid.enabled) {
      if (isInsideWindow) {
        els.radioIdWindowStatus.innerHTML = `<span style="color:#10b981;font-weight:700;">● Inside Window (:55 - :10)</span>`;
      } else {
        els.radioIdWindowStatus.textContent = `Window Status: Standby (:55 - :10)`;
      }
    } else {
      els.radioIdWindowStatus.textContent = 'Radio IDs Disabled';
    }
  }

  // ----------------------------------------------------
  // Playback & Automation Execution (Gapless & Fades)
  // ----------------------------------------------------
  function renderCurrentTrack() {
    const t = state.currentTrack;
    els.trackTitle.textContent = t.artist ? `${t.artist} - ${t.title}` : t.title;
    els.trackMeta.textContent = `Artist: ${t.artist || 'Station'} • Album: ${t.album || 'Output'} • Tag: ${t.category || 'General'}`;
    els.trackTagBadge.textContent = t.category || 'General';

    if (t.type === 'sweeper') {
      els.heroIcon.textContent = '⚡';
      els.trackTagBadge.className = 'hero-tag-badge type-badge sweeper';
    } else if (t.type === 'radioid') {
      els.heroIcon.textContent = '📻';
      els.trackTagBadge.className = 'hero-tag-badge type-badge radioid';
    } else {
      els.heroIcon.textContent = '🎵';
      els.trackTagBadge.className = 'hero-tag-badge type-badge song';
    }

    updateTrackTimers();
    updateNextUpPreview();
    updateHeader();
  }

  function updateTrackTimers() {
    const t = state.currentTrack;
    const elapsed = formatSeconds(t.position);
    const total = formatSeconds(t.duration);
    const remaining = formatSeconds(Math.max(0, t.duration - t.position));

    els.timeElapsed.textContent = elapsed;
    els.timeTotal.textContent = total;
    els.timeRemaining.textContent = `(-${remaining})`;

    const pct = t.duration > 0 ? (t.position / t.duration) * 100 : 0;
    els.progressBar.style.width = pct + '%';
  }

  function updateNextUpPreview() {
    if (state.playlist.length > 0) {
      const next = state.playlist[0];
      const icon = next.type === 'sweeper' ? '⚡ ' : (next.type === 'radioid' ? '📻 ' : '🎵 ');
      els.nextUpPreview.textContent = `Next: ${icon}${next.artist} - ${next.title}`;
    } else {
      els.nextUpPreview.textContent = 'Next: (AutoDJ queueing)';
    }
  }

  function skipToNext() {
    if (state.playlist.length === 0) {
      return;
    }

    const nextTrack = state.playlist.shift();
    renderPlaylist();

    state.currentTrack = {
      id: nextTrack.id,
      type: nextTrack.type || 'song',
      title: nextTrack.title,
      artist: nextTrack.artist,
      album: nextTrack.album || 'Station Output',
      category: nextTrack.category || 'Music',
      duration: typeof nextTrack.duration === 'string' ? parseDuration(nextTrack.duration) : nextTrack.duration,
      position: 0,
      state: 'PLAYING',
      waveformPeaks: generateMockWaveform(200)
    };

    renderCurrentTrack();
    initWaveform();
    updateHourCoverage();
  }

  function insertSweeperNext() {
    window.radioApi.sendCommand('AUTO_SCHEDULE_NOW');
  }

  function insertRadioIdNext() {
    window.radioApi.sendCommand('AUTO_SCHEDULE_NOW');
  }

  // ----------------------------------------------------
  // Audio & Animation Loop (60 FPS)
  // ----------------------------------------------------
  function initAudioLoop() {
    let lastTime = performance.now();
    let lastRender = 0;

    function frame(now) {
      requestAnimationFrame(frame);

      // Throttle to 20 FPS (~50ms) to ensure minimal CPU usage on 1-vCPU VMs
      if (now - lastRender < 50) return;
      lastRender = now;

      const delta = (now - lastTime) / 1000;
      lastTime = now;

      const t = state.currentTrack;
      if (t.state === 'PLAYING' && t.duration > 0) {
        t.position += delta;
        if (t.position >= t.duration) {
          t.position = t.duration;
        }
        updateTrackTimers();
      }

      // VU Meters & Normalization DSP
      renderVuMeters();

      // Uptime
      state.uptimeSeconds += delta;
      els.streamUptime.textContent = formatUptime(Math.floor(state.uptimeSeconds));
    }
    requestAnimationFrame(frame);
  }

  function renderVuMeters() {
    let activeLevel = 0;
    if (state.currentTrack.state === 'PLAYING') {
      const baseAmp = 0.78 + Math.sin(performance.now() * 0.02) * 0.12 + (Math.random() * 0.05);
      const ducking = state.micDucking ? 0.35 : 1.0;
      activeLevel = Math.min(Math.max(baseAmp * ducking * 100, 5), 95);

      if (els.realtimeLoudness) {
        const peakDb = (-3.0 + (Math.random() * 0.4 - 0.2)).toFixed(1);
        els.realtimeLoudness.textContent = `${peakDb} dB Peak (Limiter Active)`;
      }
    }

    const left = Math.max(0, activeLevel + (Math.random() * 4 - 2));
    const right = Math.max(0, activeLevel + (Math.random() * 4 - 2));

    els.vuLeft.style.width = left + '%';
    els.vuRight.style.width = right + '%';
  }

  function updateHeader() {
    if (state.isBroadcasting) {
      els.streamPill.dataset.status = 'connected';
      els.streamStatusText.textContent = 'LIVE ON AIR';
      els.btnToggleBroadcast.classList.add('broadcasting');
      els.btnBroadcastText.textContent = 'STOP STREAM';
    } else {
      els.streamPill.dataset.status = 'disconnected';
      els.streamStatusText.textContent = 'OFFLINE';
      els.btnToggleBroadcast.classList.remove('broadcasting');
      els.btnBroadcastText.textContent = 'START STREAM';
    }

    const t = state.currentTrack;
    els.onAirTitle.textContent = t.artist ? `${t.artist} - ${t.title}` : t.title;
    els.onAirTypeTag.textContent = (t.type || 'song').toUpperCase();
  }

  // ----------------------------------------------------
  // Renderers & Filters
  // ----------------------------------------------------
  function renderPlaylist() {
    els.playlistTableBody.innerHTML = '';
    els.playlistCount.textContent = state.playlist.length;

    if (state.playlist.length === 0) {
      els.playlistTableBody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:24px;color:var(--text-muted);">Broadcast queue is empty.</td></tr>';
      return;
    }

    state.playlist.forEach((item, index) => {
      const tr = document.createElement('tr');
      const typeClass = item.type || 'song';
      const typeLabel = item.type === 'sweeper' ? 'JINGLE' : (item.type === 'radioid' ? 'RADIO ID' : 'SONG');
      const durStr = typeof item.duration === 'string' ? item.duration : formatSeconds(item.duration);

      tr.innerHTML = `
        <td class="font-mono" style="color:var(--text-muted);">${(index + 1).toString().padStart(2, '0')}</td>
        <td><span class="type-badge ${typeClass}">${typeLabel}</span></td>
        <td>
          <div style="font-weight:600;">${item.title}</div>
          <div style="font-size:0.72rem;color:var(--text-secondary);">${item.artist || 'Station'}</div>
        </td>
        <td class="font-mono">${durStr}</td>
        <td><span class="badge-tag-highlight">${item.category || 'General'}</span></td>
        <td class="text-right">
          <button class="btn-table-action btn-play-now" data-idx="${index}">Play</button>
          <button class="btn-remove-row" data-idx="${index}">&times;</button>
        </td>
      `;

      tr.querySelector('.btn-play-now').addEventListener('click', () => {
        window.radioApi.deckPlay('A');
      });

      tr.querySelector('.btn-remove-row').addEventListener('click', () => {
        state.playlist.splice(index, 1);
        renderPlaylist();
        updateHourCoverage();
      });

      els.playlistTableBody.appendChild(tr);
    });
  }

  function renderLibrary(tracks) {
    els.libraryTableBody.innerHTML = '';
    if (!tracks || tracks.length === 0) {
      els.libraryTableBody.innerHTML = '<tr><td colspan="5" style="text-align:center;padding:24px;color:var(--text-muted);">No tracks in library yet. Add a watched folder or click Rescan Library.</td></tr>';
      return;
    }

    tracks.forEach(track => {
      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td>
          <div style="font-weight:600;">${track.title}</div>
          <div style="font-size:0.72rem;color:var(--text-secondary);">${track.artist || 'Unknown'}</div>
        </td>
        <td>${track.album || ''}</td>
        <td class="font-mono">${track.duration}</td>
        <td><span class="badge-tag-highlight">${track.category || 'General'}</span></td>
        <td class="text-right">
          <button class="btn-table-action btn-edit-track" data-id="${track.id}">Edit</button>
        </td>
      `;

      tr.querySelector('.btn-edit-track').addEventListener('click', () => openTrackEdit(track));
      els.libraryTableBody.appendChild(tr);
    });
  }

  function applyLibraryFilters() {
    const q = els.librarySearchInput.value.toLowerCase().trim();
    const cat = els.categoryFilterSelect.value;

    let filtered = state.library;
    if (cat !== 'ALL') {
      filtered = filtered.filter(t => t.category === cat);
    }
    if (q) {
      filtered = filtered.filter(t => 
        (t.title && t.title.toLowerCase().includes(q)) ||
        (t.artist && t.artist.toLowerCase().includes(q)) ||
        (t.album && t.album.toLowerCase().includes(q)) ||
        (t.category && t.category.toLowerCase().includes(q))
      );
    }
    renderLibrary(filtered);
  }

  function renderWatchedFolders() {
    els.watchedFoldersList.innerHTML = '';
    if (state.watchedFolders.length === 0) {
      els.watchedFoldersList.innerHTML = '<li style="padding:10px;color:var(--text-muted);font-size:0.75rem;">No watched folders configured yet.</li>';
      return;
    }

    state.watchedFolders.forEach((folder, idx) => {
      const li = document.createElement('li');
      li.className = 'folder-item';
      li.innerHTML = `
        <span class="font-mono">${folder}</span>
        <button class="btn-remove-folder" data-idx="${idx}">&times;</button>
      `;
      li.querySelector('.btn-remove-folder').addEventListener('click', () => {
        state.watchedFolders.splice(idx, 1);
        renderWatchedFolders();
      });
      els.watchedFoldersList.appendChild(li);
    });
  }

  function renderSweepers() {
    els.sweepersGrid.innerHTML = '';
    state.sweepers.forEach(sw => {
      const card = document.createElement('div');
      card.className = 'sweeper-card';
      card.innerHTML = `
        <div class="sweeper-card-header">
          <span class="type-badge ${sw.title.includes('Station ID') ? 'radioid' : 'sweeper'}">${sw.title.includes('Station ID') ? 'RADIO ID' : 'SWEEPER'}</span>
          <span class="font-mono" style="font-size:0.75rem;color:var(--text-muted);">${sw.duration}s</span>
        </div>
        <div class="sweeper-title">${sw.title}</div>
        <div class="sweeper-actions">
          <button class="btn-action btn-quick-next" style="flex:1;">Insert Next</button>
        </div>
      `;
      els.sweepersGrid.appendChild(card);
    });
  }

  // ----------------------------------------------------
  // Track Editor & Settings
  // ----------------------------------------------------
  function openTrackEdit(track) {
    state.editingTrackId = track.id;
    els.editTrackTitle.value = track.title;
    els.editTrackArtist.value = track.artist;
    els.editTrackAlbum.value = track.album;
    els.editTrackCategory.value = track.category;
    els.editTrackYear.value = track.year || 2026;
    els.editTrackCueIn.value = track.cueIn || 0.0;
    els.editTrackCueOut.value = track.cueOut || parseDuration(track.duration);
    els.trackEditModal.classList.add('open');
  }

  function saveTrackEdit() {
    const track = state.library.find(t => t.id === state.editingTrackId);
    if (track) {
      track.title = els.editTrackTitle.value;
      track.artist = els.editTrackArtist.value;
      track.album = els.editTrackAlbum.value;
      track.category = els.editTrackCategory.value;
      track.year = parseInt(els.editTrackYear.value, 10);
      track.cueIn = parseFloat(els.editTrackCueIn.value);
      track.cueOut = parseFloat(els.editTrackCueOut.value);
      renderLibrary(state.library);
    }
    els.trackEditModal.classList.remove('open');
  }

  async function saveSettings() {
    state.sweeperRules.enabled = document.getElementById('cfgSweepersEnabled').value === 'true';
    state.sweeperRules.folder = document.getElementById('cfgSweeperFolder').value;
    state.sweeperRules.minSongs = parseInt(document.getElementById('cfgSweeperMin').value, 10);
    state.sweeperRules.maxSongs = parseInt(document.getElementById('cfgSweeperMax').value, 10);

    state.radioIdRules.enabled = document.getElementById('cfgRadioIdsEnabled').value === 'true';
    state.radioIdRules.folder = document.getElementById('cfgRadioIdFolder').value;
    state.radioIdRules.targetMinute = parseInt(document.getElementById('cfgRadioIdMinute').value, 10);
    state.radioIdRules.toleranceBefore = parseInt(document.getElementById('cfgToleranceBefore').value, 10);
    state.radioIdRules.toleranceAfter = parseInt(document.getElementById('cfgToleranceAfter').value, 10);

    state.autoDjFillCurrentHour = document.getElementById('cfgFillCurrentHour').value === 'true';
    state.autoDjDefaultTag = document.getElementById('cfgDefaultTag').value.trim();

    // Stream server details
    const streamServerType = document.getElementById('cfgStreamServerType')?.value || 'Shoutcast';
    const streamHost = document.getElementById('cfgStreamHost')?.value || 'localhost';
    const streamPort = parseInt(document.getElementById('cfgStreamPort')?.value || '8000', 10);
    const streamMount = document.getElementById('cfgStreamMount')?.value || 'radio';
    const streamUsername = document.getElementById('cfgStreamUsername')?.value || 'source';
    const streamPassword = document.getElementById('cfgStreamPassword')?.value || '';
    const streamBitrate = parseInt(document.getElementById('cfgStreamBitrate')?.value || '128', 10);
    const streamShoutcastStreamId = parseInt(document.getElementById('cfgStreamShoutcastId')?.value || '1', 10);
    const autoConnectStreamOnStartup = document.getElementById('cfgAutoConnectStream')?.value === 'true';

    // Parse raw text rules
    const rawRules = document.getElementById('cfgHourlyTagRulesRaw').value;
    parseRawRulesString(rawRules);

    // Save to C# backend (persists in settings.json)
    await window.radioApi.saveSettings({
      sweepersEnabled: state.sweeperRules.enabled,
      sweeperFolder: state.sweeperRules.folder,
      sweeperMin: state.sweeperRules.minSongs,
      sweeperMax: state.sweeperRules.maxSongs,
      radioIdsEnabled: state.radioIdRules.enabled,
      radioIdFolder: state.radioIdRules.folder,
      radioIdMinute: state.radioIdRules.targetMinute,
      toleranceBefore: state.radioIdRules.toleranceBefore,
      toleranceAfter: state.radioIdRules.toleranceAfter,
      autoDjFillCurrentHour: state.autoDjFillCurrentHour,
      autoDjDefaultTag: state.autoDjDefaultTag,
      hourlyTagRules: rawRules,
      streamServerType,
      streamHost,
      streamPort,
      streamMount,
      streamUsername,
      streamPassword,
      streamBitrate,
      streamShoutcastStreamId,
      autoConnectStreamOnStartup
    });

    updateRuleUI();
    updateRotationStatus();
    updateHourCoverage();
    els.settingsModal.classList.remove('open');
    alert('Station rules and streaming configuration saved.');
  }

  function parseRawRulesString(raw) {
    if (!raw) return;
    const lines = raw.split(/[\n;]/).map(s => s.trim()).filter(Boolean);
    const parsed = [];
    lines.forEach((line, idx) => {
      const parts = line.split('=').map(s => s.trim());
      if (parts.length === 2) {
        const range = parts[0].split('-').map(s => s.trim());
        if (range.length === 2) {
          parsed.push({
            id: Date.now() + idx,
            startTime: range[0].includes(':') ? range[0] : `${range[0].padStart(2, '0')}:00`,
            endTime: range[1].includes(':') ? range[1] : `${range[1].padStart(2, '0')}:00`,
            tag: parts[1],
            title: `${parts[1]} Rotation`,
            desc: `Hourly rotation for ${parts[1]}`
          });
        }
      }
    });
    if (parsed.length > 0) state.scheduleRules = parsed;
  }

  // ----------------------------------------------------
  // Waveform Visualizer
  // ----------------------------------------------------
  function initWaveform() {
    const canvas = els.waveformCanvas;
    const ctx = canvas.getContext('2d');
    canvas.width = canvas.parentElement.clientWidth * window.devicePixelRatio;
    canvas.height = canvas.parentElement.clientHeight * window.devicePixelRatio;
    ctx.scale(window.devicePixelRatio, window.devicePixelRatio);

    const peaks = state.currentTrack.waveformPeaks;
    const width = canvas.parentElement.clientWidth;
    const height = canvas.parentElement.clientHeight;

    ctx.clearRect(0, 0, width, height);
    const barWidth = width / peaks.length;
    ctx.fillStyle = state.currentTrack.type === 'sweeper' ? '#f59e0b' : (state.currentTrack.type === 'radioid' ? '#ec4899' : '#38bdf8');
    peaks.forEach((peak, i) => {
      const barH = peak * (height * 0.8);
      const y = (height - barH) / 2;
      ctx.fillRect(i * barWidth, y, Math.max(barWidth - 1, 1), barH);
    });
  }

  function generateMockWaveform(samples) {
    const peaks = [];
    for (let i = 0; i < samples; i++) {
      const val = 0.2 + Math.abs(Math.sin(i * 0.12)) * 0.5 + Math.random() * 0.3;
      peaks.push(Math.min(val, 1.0));
    }
    return peaks;
  }

  // ----------------------------------------------------
  // Tabs & Navigation
  // ----------------------------------------------------
  function initTabs() {
    const tabBtns = document.querySelectorAll('.manager-nav .tab-btn');
    tabBtns.forEach(btn => {
      btn.addEventListener('click', () => {
        tabBtns.forEach(b => b.classList.remove('active'));
        document.querySelectorAll('.tab-pane').forEach(p => p.classList.remove('active'));
        btn.classList.add('active');
        const target = document.getElementById(`pane-${btn.dataset.tab}`);
        if (target) target.classList.add('active');
        if (btn.dataset.tab === 'schedule') updateRotationStatus();
        if (btn.dataset.tab === 'playlist') updateHourCoverage();
      });
    });
  }

  function initMobileNav() {
    const navBtns = document.querySelectorAll('.mobile-nav-btn');
    navBtns.forEach(btn => {
      btn.addEventListener('click', () => {
        navBtns.forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        const view = btn.dataset.mobileView;

        if (view === 'player') {
          document.getElementById('programDeckSection').scrollIntoView({ behavior: 'smooth' });
        } else if (view === 'settings') {
          els.settingsModal.classList.add('open');
        } else {
          const tabBtn = document.querySelector(`.tab-btn[data-tab="${view}"]`);
          if (tabBtn) tabBtn.click();
          document.querySelector('.studio-manager-section').scrollIntoView({ behavior: 'smooth' });
        }
      });
    });
  }

  function formatSeconds(sec) {
    const s = Math.floor(sec % 60);
    const m = Math.floor(sec / 60);
    return `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
  }

  function formatUptime(totalSec) {
    const h = Math.floor(totalSec / 3600);
    const m = Math.floor((totalSec % 3600) / 60);
    const s = totalSec % 60;
    return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
  }

  function parseDuration(str) {
    if (typeof str === 'number') return str;
    const parts = str.split(':').map(Number);
    return parts.length === 2 ? parts[0] * 60 + parts[1] : 180;
  }
});
