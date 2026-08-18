/**
 * RadioApp Studio - Single Deck Broadcast & Rule Engine Logic
 * Built for:
 * 1. Hourly Tag Rotation
 * 2. Gapless Playback & Silence Trimming (-42dB)
 * 3. Adaptive Normalization DSP (-3.0dB target peak)
 * 4. Hour-Filling Calculation (Aligns Radio ID to :00 within tolerance)
 * 5. Automated Sweeper / Jingle Insertions
 */

document.addEventListener('DOMContentLoaded', () => {
  // State
  const state = {
    isBroadcasting: false,
    autoDj: true,
    autoDjFillCurrentHour: true,
    micDucking: false,
    listeners: 38,
    peakListeners: 84,
    uptimeSeconds: 8120,
    
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
    watchedFolders: [
      'D:\\Music\\MainLibrary',
      'D:\\Music\\NewRotations',
      'D:\\Music\\PuertoRicoSalsa'
    ],

    // Hourly Tag Rotation Rules
    scheduleRules: [
      { id: 1, startTime: '08:00', endTime: '12:00', tag: 'Salsa', title: 'Salsa Matutina', desc: 'Classic Puerto Rico salsa with jingles every 3-6 songs.' },
      { id: 2, startTime: '12:00', endTime: '16:00', tag: 'Rock', title: 'Rock en Español', desc: '80s and 90s Spanish rock favorites.' },
      { id: 3, startTime: '16:00', endTime: '20:00', tag: 'Reggaeton', title: 'Tarde Urbana', desc: 'High energy reggaeton and urban hits.' },
      { id: 4, startTime: '20:00', endTime: '08:00', tag: 'Romántica', title: 'Noche de Recuerdos', desc: 'Overnight romantic ballads and classic tracks.' }
    ],

    autoDjDefaultTag: 'Salsa',
    autoDjQueueDepth: 5,

    // On-Air Track
    currentTrack: {
      id: 1,
      type: 'song',
      title: 'Brujería',
      artist: 'El Gran Combo de Puerto Rico',
      album: 'De Punta a Punta (1971)',
      category: 'Salsa',
      duration: 215,
      position: 68,
      cueIn: 0.0,
      cueOut: 215.0,
      state: 'PLAYING',
      waveformPeaks: generateMockWaveform(200)
    },

    // Live Broadcast Queue (Pre-calculated for the current hour)
    playlist: [
      { id: 2, type: 'sweeper', title: 'GLZ Sweeper Puerto Rico 01', artist: 'Station Branding', album: 'Sweepers', category: 'Jingle', duration: 8 },
      { id: 3, type: 'song', title: 'Periódico de Ayer', artist: 'Héctor Lavoe', album: 'De Ti Depende (1976)', category: 'Salsa', duration: 310 },
      { id: 4, type: 'song', title: 'Fuego en el 23', artist: 'La Sonora Ponceña', album: 'Fuego en el 23', category: 'Salsa', duration: 275 },
      { id: 5, type: 'song', title: 'Desnúdate Mujer', artist: 'Frankie Ruiz', album: '¡Voy Pa\' Encima!', category: 'Salsa', duration: 290 },
      { id: 6, type: 'radioid', title: 'Top of the Hour Station ID (Legal)', artist: 'GLZ Radio Official', album: 'Station IDs', category: 'StationID', duration: 15 },
      { id: 7, type: 'song', title: 'Pa Que Retozen', artist: 'Tego Calderón', album: 'El Abayarde', category: 'Reggaeton', duration: 212 }
    ],

    // Media Library Database
    library: [
      { id: 10, type: 'song', title: 'Brujería', artist: 'El Gran Combo de Puerto Rico', album: 'De Punta a Punta', duration: '03:35', category: 'Salsa', year: 1971, cueIn: 0.0, cueOut: 215.0 },
      { id: 11, type: 'song', title: 'Periódico de Ayer', artist: 'Héctor Lavoe', album: 'De Ti Depende', duration: '05:10', category: 'Salsa', year: 1976, cueIn: 0.0, cueOut: 310.0 },
      { id: 12, type: 'song', title: 'Fuego en el 23', artist: 'La Sonora Ponceña', album: 'Fuego en el 23', duration: '04:35', category: 'Salsa', year: 1969, cueIn: 0.0, cueOut: 275.0 },
      { id: 13, type: 'song', title: 'Desnúdate Mujer', artist: 'Frankie Ruiz', album: '¡Voy Pa\' Encima!', duration: '04:50', category: 'Salsa', year: 1987, cueIn: 0.0, cueOut: 290.0 },
      { id: 14, type: 'song', title: 'Idilio', artist: 'Willie Colón', album: 'Top Salsa', duration: '05:04', category: 'Salsa', year: 1993, cueIn: 0.0, cueOut: 304.0 },
      { id: 15, type: 'song', title: 'Indestructible', artist: 'Ray Barretto', album: 'Indestructible', duration: '04:14', category: 'Salsa', year: 1973, cueIn: 0.0, cueOut: 254.0 },
      { id: 16, type: 'song', title: 'Pa Que Retozen', artist: 'Tego Calderón', album: 'El Abayarde', duration: '03:32', category: 'Reggaeton', year: 2002, cueIn: 0.0, cueOut: 212.0 },
      { id: 17, type: 'song', title: 'Gasolina', artist: 'Daddy Yankee', album: 'Barrio Fino', duration: '03:12', category: 'Reggaeton', year: 2004, cueIn: 0.0, cueOut: 192.0 },
      { id: 18, type: 'song', title: 'Dile', artist: 'Don Omar', album: 'The Last Don', duration: '03:25', category: 'Reggaeton', year: 2003, cueIn: 0.0, cueOut: 205.0 },
      { id: 19, type: 'song', title: 'Lamento Boliviano', artist: 'Enanitos Verdes', album: 'Big Yuyo', duration: '03:54', category: 'Rock', year: 1994, cueIn: 0.0, cueOut: 234.0 },
      { id: 20, type: 'song', title: 'De Música Ligera', artist: 'Soda Stereo', album: 'Canción Animal', duration: '03:36', category: 'Rock', year: 1990, cueIn: 0.0, cueOut: 216.0 },
      { id: 21, type: 'song', title: 'En el Muelle de San Blas', artist: 'Maná', album: 'Sueños Líquidos', duration: '05:51', category: 'Rock', year: 1997, cueIn: 0.0, cueOut: 351.0 }
    ],

    // Sweepers & Radio IDs
    sweepers: [
      { id: 201, title: 'Transición En Vivo Sweep 01', duration: 8, folder: 'D:\\Radio\\Sweepers' },
      { id: 202, title: 'GLZ Radio La Que Suena Sweep 02', duration: 6, folder: 'D:\\Radio\\Sweepers' },
      { id: 203, title: 'Salsa Pura Jingle Stinger', duration: 10, folder: 'D:\\Radio\\Sweepers' },
      { id: 204, title: 'Top of the Hour Station ID (Legal)', duration: 15, folder: 'D:\\Radio\\RadioIDs' }
    ],

    // Virtual Server File System for Directory Browser
    serverFileSystem: {
      'D:\\': ['Radio', 'Music', 'Backups', 'Recordings'],
      'D:\\Radio': ['Sweepers', 'RadioIDs', 'VoiceTracks', 'Promos'],
      'D:\\Radio\\Sweepers': ['MorningSweeps', 'WeekendSweeps', 'Stingers'],
      'D:\\Radio\\RadioIDs': ['LegalIDs', 'TopHourIDs', 'QuickIDs'],
      'D:\\Music': ['MainLibrary', 'NewRotations', 'PuertoRicoSalsa', 'RockEnEspanol', 'UrbanReggaeton'],
      'C:\\': ['Users', 'Program Files', 'RadioApp', 'AppData'],
      'C:\\RadioApp': ['Database', 'Cache', 'Logs'],
      'Z:\\': ['SharedMedia', 'StudioArchive', 'MasterFLAC']
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
  window.radioApi.init();

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

    els.libraryCount.textContent = state.library.length;
    els.listenerCount.textContent = state.listeners;

    // Transport Controls
    els.btnPlay.addEventListener('click', () => {
      state.currentTrack.state = 'PLAYING';
      els.playerState.textContent = 'PLAYING';
      updateHeader();
      window.radioApi.deckPlay('A');
    });

    els.btnPause.addEventListener('click', () => {
      state.currentTrack.state = 'PAUSED';
      els.playerState.textContent = 'PAUSED';
      window.radioApi.deckPause('A');
    });

    els.btnStop.addEventListener('click', () => {
      state.currentTrack.state = 'STOPPED';
      state.currentTrack.position = 0;
      els.playerState.textContent = 'STOPPED';
      updateTrackTimers();
      window.radioApi.deckStop('A');
    });

    els.btnCue.addEventListener('click', () => {
      state.currentTrack.state = 'CUED';
      state.currentTrack.position = state.currentTrack.cueIn || 0;
      els.playerState.textContent = 'CUED';
      updateTrackTimers();
      window.radioApi.deckCue('A');
    });

    els.btnSkip.addEventListener('click', skipToNext);

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
      state.isBroadcasting = !state.isBroadcasting;
      updateHeader();
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

    // Import / Rescan Library
    els.btnImportTrack.addEventListener('click', () => {
      const title = prompt('Track Title:', 'New Track');
      const artist = prompt('Artist Name:', 'Artist');
      if (title && artist) {
        state.library.unshift({
          id: Date.now(),
          type: 'song',
          title,
          artist,
          album: 'Single',
          duration: '03:30',
          category: 'Salsa',
          year: 2026,
          cueIn: 0.0,
          cueOut: 210.0
        });
        renderLibrary(state.library);
        els.libraryCount.textContent = state.library.length;
      }
    });

    els.btnImportFolder.addEventListener('click', () => openFolderBrowser('library'));
    els.btnRescanLibrary.addEventListener('click', rescanLibrary);

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

    // Find candidates matching current rotation
    let candidates = state.library.filter(t => 
      (t.category && t.category.toLowerCase() === targetTag) ||
      (t.album && t.album.toLowerCase().includes(targetTag))
    );
    if (candidates.length === 0) candidates = state.library;

    // Remaining time in the current clock hour
    const now = new Date();
    const secondsPassedInHour = now.getMinutes() * 60 + now.getSeconds();
    const remainingInHour = 3600 - secondsPassedInHour;

    // Current playing track remainder
    let currentCoverage = Math.max(0, state.currentTrack.duration - state.currentTrack.position);
    state.playlist.forEach(item => {
      currentCoverage += (typeof item.duration === 'string' ? parseDuration(item.duration) : item.duration);
    });

    let songsAdded = 0;
    const shuffled = [...candidates].sort(() => Math.random() - 0.5);

    // Add tracks until reaching the top of the hour
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

      // Inject planned sweeper if interval reached
      if (songsAdded % 4 === 0 && state.sweeperRules.enabled) {
        const sw = state.sweepers[Math.floor(Math.random() * 3)];
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

    // Insert Top of the Hour Radio ID at the end of the calculated hour
    if (state.radioIdRules.enabled && !state.playlist.some(p => p.type === 'radioid')) {
      state.playlist.push({
        id: Date.now() + 9999,
        type: 'radioid',
        title: 'Top of the Hour Station ID (Legal)',
        artist: 'GLZ Radio Official',
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

    // Projected Radio ID trigger time
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
  // Interactive Server Folder Browser
  // ----------------------------------------------------
  function initFolderBrowser() {
    els.btnCloseBrowserModal.addEventListener('click', () => els.folderBrowserModal.classList.remove('open'));
    els.btnCancelBrowser.addEventListener('click', () => els.folderBrowserModal.classList.remove('open'));

    // Drive selector
    els.driveButtons.querySelectorAll('.btn-drive').forEach(btn => {
      btn.addEventListener('click', () => {
        els.driveButtons.querySelectorAll('.btn-drive').forEach(b => b.classList.remove('active'));
        btn.classList.add('active');
        state.browserState.currentDrive = btn.dataset.drive;
        state.browserState.currentPath = btn.dataset.drive;
        state.browserState.selectedPath = btn.dataset.drive;
        renderBrowserDirectory();
      });
    });

    // Confirm button
    els.btnConfirmSelectFolder.addEventListener('click', () => {
      const path = state.browserState.selectedPath;
      if (state.browserState.targetType === 'sweeper') {
        state.sweeperRules.folder = path;
        els.txtSweeperFolderPath.value = path;
        document.getElementById('cfgSweeperFolder').value = path;
      } else if (state.browserState.targetType === 'radioid') {
        state.radioIdRules.folder = path;
        els.txtRadioIdFolderPath.value = path;
        document.getElementById('cfgRadioIdFolder').value = path;
      } else if (state.browserState.targetType === 'library') {
        if (!state.watchedFolders.includes(path)) {
          state.watchedFolders.push(path);
          renderWatchedFolders();
          rescanLibrary();
        }
      }
      updateRuleUI();
      els.folderBrowserModal.classList.remove('open');
    });
  }

  function openFolderBrowser(targetType) {
    state.browserState.targetType = targetType;
    if (targetType === 'sweeper') {
      els.browserModalTitle.textContent = 'Select Jingles & Sweepers Directory';
      state.browserState.selectedPath = state.sweeperRules.folder || 'D:\\Radio\\Sweepers';
    } else if (targetType === 'radioid') {
      els.browserModalTitle.textContent = 'Select Radio IDs Directory';
      state.browserState.selectedPath = state.radioIdRules.folder || 'D:\\Radio\\RadioIDs';
    } else {
      els.browserModalTitle.textContent = 'Select Music Library Folder to Watch';
      state.browserState.selectedPath = 'D:\\Music';
    }

    state.browserState.currentPath = state.browserState.selectedPath;
    renderBrowserDirectory();
    els.folderBrowserModal.classList.add('open');
  }

  function renderBrowserDirectory() {
    const p = state.browserState.currentPath;
    els.txtSelectedBrowserPath.value = state.browserState.selectedPath;

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
      span.addEventListener('click', () => {
        state.browserState.currentPath = targetP.includes('\\') ? targetP : targetP + '\\';
        state.browserState.selectedPath = state.browserState.currentPath;
        renderBrowserDirectory();
      });
      els.breadcrumbBar.appendChild(span);
    });

    // Subdirectories list
    els.directoryList.innerHTML = '';

    if (p !== 'C:\\' && p !== 'D:\\' && p !== 'Z:\\') {
      const upLi = document.createElement('li');
      upLi.className = 'dir-item';
      upLi.innerHTML = `<span>⬆️</span> <strong>.. (Up One Level)</strong>`;
      upLi.addEventListener('click', () => {
        const lastSlash = p.lastIndexOf('\\');
        const parentP = lastSlash > 2 ? p.substring(0, lastSlash) : p.substring(0, 3);
        state.browserState.currentPath = parentP;
        state.browserState.selectedPath = parentP;
        renderBrowserDirectory();
      });
      els.directoryList.appendChild(upLi);
    }

    const subdirs = state.serverFileSystem[p] || ['SubFolder01', 'SubFolder02', 'Archive'];
    subdirs.forEach(folderName => {
      const fullSubPath = p.endsWith('\\') ? p + folderName : p + '\\' + folderName;
      const li = document.createElement('li');
      li.className = `dir-item ${state.browserState.selectedPath === fullSubPath ? 'selected' : ''}`;
      li.innerHTML = `<span>📁</span> <span>${folderName}</span>`;

      li.addEventListener('click', () => {
        state.browserState.selectedPath = fullSubPath;
        els.txtSelectedBrowserPath.value = fullSubPath;
        els.directoryList.querySelectorAll('.dir-item').forEach(d => d.classList.remove('selected'));
        li.classList.add('selected');
      });

      li.addEventListener('dblclick', () => {
        state.browserState.currentPath = fullSubPath;
        state.browserState.selectedPath = fullSubPath;
        renderBrowserDirectory();
      });

      els.directoryList.appendChild(li);
    });
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

  function rescanLibrary() {
    els.librarySyncStatusText.textContent = 'Scanning watched directories for audio tags...';
    setTimeout(() => {
      const added = Math.floor(Math.random() * 8) + 2;
      els.librarySyncStatusText.textContent = `Library Sync Complete: +${added} tracks added across ${state.watchedFolders.length} watched folders.`;
    }, 600);
  }

  // ----------------------------------------------------
  // Playback & Automation Execution (Gapless & Fades)
  // ----------------------------------------------------
  function renderCurrentTrack() {
    const t = state.currentTrack;
    els.trackTitle.textContent = `${t.artist} - ${t.title}`;
    els.trackMeta.textContent = `Artist: ${t.artist} • Album: ${t.album} • Tag: ${t.category}`;
    els.trackTagBadge.textContent = t.category;

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
      els.nextUpPreview.textContent = `Next: ${icon}${next.artist} - ${next.title} (Preloaded)`;
    } else {
      els.nextUpPreview.textContent = 'Next: (AutoDJ will fill current hour)';
    }
  }

  function skipToNext() {
    if (state.playlist.length === 0) {
      if (state.autoDj) {
        calculateAndFillCurrentHour();
      } else {
        state.currentTrack.state = 'STOPPED';
        state.currentTrack.position = 0;
        updateTrackTimers();
        return;
      }
    }

    const nextTrack = state.playlist.shift();
    renderPlaylist();

    if (state.currentTrack.type === 'song') {
      state.sweeperRules.songsSinceLast++;
      updateRuleUI();
    }

    state.currentTrack = {
      id: nextTrack.id,
      type: nextTrack.type || 'song',
      title: nextTrack.title,
      artist: nextTrack.artist,
      album: nextTrack.album || 'Station Output',
      category: nextTrack.category || 'Music',
      duration: typeof nextTrack.duration === 'string' ? parseDuration(nextTrack.duration) : nextTrack.duration,
      position: nextTrack.cueIn || 0,
      cueIn: nextTrack.cueIn || 0,
      cueOut: nextTrack.cueOut || nextTrack.duration,
      state: 'PLAYING',
      waveformPeaks: generateMockWaveform(200)
    };

    renderCurrentTrack();
    initWaveform();
    updateHourCoverage();

    // Check automated sweeper trigger
    const sw = state.sweeperRules;
    if (sw.enabled && sw.songsSinceLast >= sw.targetInterval) {
      insertSweeperNext();
      sw.songsSinceLast = 0;
      sw.targetInterval = Math.floor(Math.random() * (sw.maxSongs - sw.minSongs + 1)) + sw.minSongs;
      updateRuleUI();
    }
  }

  function insertSweeperNext() {
    const sw = state.sweepers[Math.floor(Math.random() * 3)];
    state.playlist.unshift({
      id: Date.now(),
      type: 'sweeper',
      title: sw.title,
      artist: 'Station Jingles',
      album: 'Sweepers',
      category: 'Jingle',
      duration: sw.duration
    });
    renderPlaylist();
    updateHourCoverage();
    updateNextUpPreview();
  }

  function insertRadioIdNext() {
    state.playlist.unshift({
      id: Date.now(),
      type: 'radioid',
      title: 'Top of the Hour Station ID (Legal)',
      artist: 'GLZ Radio Puerto Rico',
      album: 'Station IDs',
      category: 'StationID',
      duration: 15
    });
    renderPlaylist();
    updateHourCoverage();
    updateNextUpPreview();
  }

  // ----------------------------------------------------
  // Audio & Animation Loop (60 FPS, Gapless Cut at Tail)
  // ----------------------------------------------------
  function initAudioLoop() {
    let lastTime = performance.now();

    function frame(now) {
      const delta = (now - lastTime) / 1000;
      lastTime = now;

      const t = state.currentTrack;
      if (t.state === 'PLAYING' && t.duration > 0) {
        t.position += delta;

        // Gapless Silence Detection Trigger (-42dB tail cut)
        const gaplessCutPoint = Math.max(0, t.duration - (state.gapless.enabled ? 0.8 : 0.0));
        if (t.position >= gaplessCutPoint) {
          skipToNext(); // Seamless transition into next track
        } else {
          updateTrackTimers();
        }
      }

      // VU Meters & Normalization DSP
      renderVuMeters();

      // Uptime
      state.uptimeSeconds += delta;
      els.streamUptime.textContent = formatUptime(Math.floor(state.uptimeSeconds));

      requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
  }

  function renderVuMeters() {
    let activeLevel = 0;
    if (state.currentTrack.state === 'PLAYING') {
      // Regulated to broadcast target -3.0 dB
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
    els.onAirTitle.textContent = `${t.artist} - ${t.title}`;
    els.onAirTypeTag.textContent = (t.type || 'song').toUpperCase();
  }

  // ----------------------------------------------------
  // Renderers & Filters
  // ----------------------------------------------------
  function renderPlaylist() {
    els.playlistTableBody.innerHTML = '';
    els.playlistCount.textContent = state.playlist.length;

    if (state.playlist.length === 0) {
      els.playlistTableBody.innerHTML = '<tr><td colspan="6" style="text-align:center;padding:24px;color:var(--text-muted);">Playlist is empty. Auto-DJ will fill the current hour.</td></tr>';
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
          <div style="font-size:0.72rem;color:var(--text-secondary);">${item.artist}</div>
        </td>
        <td class="font-mono">${durStr}</td>
        <td><span class="badge-tag-highlight">${item.category}</span></td>
        <td class="text-right">
          <button class="btn-table-action btn-play-now" data-idx="${index}">Play Now</button>
          <button class="btn-remove-row" data-idx="${index}">&times;</button>
        </td>
      `;

      tr.querySelector('.btn-play-now').addEventListener('click', () => {
        state.playlist.splice(index, 1);
        state.currentTrack = {
          id: item.id,
          type: item.type,
          title: item.title,
          artist: item.artist,
          album: item.album || 'Station Output',
          category: item.category,
          duration: typeof item.duration === 'string' ? parseDuration(item.duration) : item.duration,
          position: 0,
          state: 'PLAYING',
          waveformPeaks: generateMockWaveform(200)
        };
        renderCurrentTrack();
        renderPlaylist();
        initWaveform();
        updateHourCoverage();
      });

      tr.querySelector('.btn-remove-row').addEventListener('click', () => {
        state.playlist.splice(index, 1);
        renderPlaylist();
        updateHourCoverage();
        updateNextUpPreview();
      });

      els.playlistTableBody.appendChild(tr);
    });
  }

  function renderLibrary(tracks) {
    els.libraryTableBody.innerHTML = '';
    tracks.forEach(track => {
      const tr = document.createElement('tr');
      tr.innerHTML = `
        <td>
          <div style="font-weight:600;">${track.title}</div>
          <div style="font-size:0.72rem;color:var(--text-secondary);">${track.artist}</div>
        </td>
        <td>${track.album}</td>
        <td class="font-mono">${track.duration}</td>
        <td><span class="badge-tag-highlight">${track.category}</span></td>
        <td class="text-right">
          <button class="btn-table-action btn-edit-track" data-id="${track.id}">Edit</button>
          <button class="btn-table-action btn-enqueue-next" data-id="${track.id}">Next</button>
          <button class="btn-table-action btn-enqueue-bottom" data-id="${track.id}">+ Queue</button>
        </td>
      `;

      tr.querySelector('.btn-edit-track').addEventListener('click', () => openTrackEdit(track));

      tr.querySelector('.btn-enqueue-next').addEventListener('click', () => {
        state.playlist.unshift({
          id: track.id,
          type: 'song',
          title: track.title,
          artist: track.artist,
          album: track.album,
          duration: parseDuration(track.duration),
          category: track.category
        });
        renderPlaylist();
        updateHourCoverage();
        updateNextUpPreview();
      });

      tr.querySelector('.btn-enqueue-bottom').addEventListener('click', () => {
        state.playlist.push({
          id: track.id,
          type: 'song',
          title: track.title,
          artist: track.artist,
          album: track.album,
          duration: parseDuration(track.duration),
          category: track.category
        });
        renderPlaylist();
        updateHourCoverage();
        updateNextUpPreview();
      });

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
        t.title.toLowerCase().includes(q) ||
        t.artist.toLowerCase().includes(q) ||
        t.album.toLowerCase().includes(q) ||
        t.category.toLowerCase().includes(q)
      );
    }
    renderLibrary(filtered);
  }

  function renderWatchedFolders() {
    els.watchedFoldersList.innerHTML = '';
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
          <button class="btn-action btn-primary btn-quick-now" style="flex:1;">Play Now</button>
        </div>
      `;

      card.querySelector('.btn-quick-next').addEventListener('click', () => {
        state.playlist.unshift({
          id: Date.now(),
          type: sw.title.includes('Station ID') ? 'radioid' : 'sweeper',
          title: sw.title,
          artist: 'Station Branding',
          album: 'Sweepers',
          duration: sw.duration,
          category: sw.title.includes('Station ID') ? 'StationID' : 'Jingle'
        });
        renderPlaylist();
        updateHourCoverage();
        updateNextUpPreview();
      });

      card.querySelector('.btn-quick-now').addEventListener('click', () => {
        state.currentTrack = {
          id: Date.now(),
          type: sw.title.includes('Station ID') ? 'radioid' : 'sweeper',
          title: sw.title,
          artist: 'Station Branding',
          album: 'Sweepers',
          category: sw.title.includes('Station ID') ? 'StationID' : 'Jingle',
          duration: sw.duration,
          position: 0,
          state: 'PLAYING',
          waveformPeaks: generateMockWaveform(200)
        };
        renderCurrentTrack();
        initWaveform();
        updateHourCoverage();
      });

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

  function saveSettings() {
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

    // Normalization & Gapless
    state.normalization.enabled = document.getElementById('cfgNormalizationEnabled').value === 'true';
    state.normalization.targetDb = parseFloat(document.getElementById('cfgNormTarget').value);
    state.gapless.silenceThresholdDb = parseFloat(document.getElementById('cfgSilenceDb').value);
    state.gapless.silenceHoldMs = parseInt(document.getElementById('cfgSilenceHold').value, 10);

    // Parse raw text rules
    const rawRules = document.getElementById('cfgHourlyTagRulesRaw').value;
    parseRawRulesString(rawRules);

    updateRuleUI();
    updateRotationStatus();
    updateHourCoverage();
    els.settingsModal.classList.remove('open');
    alert('Station rules and DSP configurations saved.');
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
