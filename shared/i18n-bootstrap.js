(function () {
  var configId = '72216192';
  var locale = 'zh-CN';

  // 注入前，C# 侧会把下面这个字面量替换成 "SYSTEM" 或 null：
  //   "SYSTEM" = 让 App 跟随**系统语言**（中文 Windows 上界面与原生菜单都会中文化，已实测）
  //   null     = **不碰** locale_source，交回 App 默认，由 localeOverride 兜底
  //              （给非中文 Windows 用 —— 那时 'SYSTEM' 会解析成 en-US、界面回英文）
  // 未被替换时（例如 macOS 端直接内嵌原始文件）回退到 'SYSTEM'：
  // 真值只可能是 "SYSTEM" 或 null，绝不会以 '_' 开头。
  var localeSource = '__LOCALE_SOURCE__';
  if (localeSource && localeSource.charAt(0) === '_') { localeSource = 'SYSTEM'; }

  var state = globalThis.__codexZhI18nState || {
    configId: configId,
    patchedClients: 0,
    patchedConfigs: 0,
    lastPatchAt: 0
  };
  try { globalThis.__codexZhI18nState = state; } catch (_) {}

  function forceConfig(config) {
    if (!config || (typeof config !== 'object' && typeof config !== 'function')) return config;
    if (config.__codexZhI18nConfig) return config;
    try {
      Object.defineProperty(config, '__codexZhI18nConfig', { value: true, configurable: true });
    } catch (_) {}
    if (typeof config.get === 'function') {
      var originalGet = config.get;
      try {
        Object.defineProperty(config, 'get', {
          configurable: true,
          value: function (key, fallback) {
            if (key === 'enable_i18n') return true;
            if (localeSource !== null && key === 'locale_source') return localeSource;
            return originalGet.call(this, key, fallback);
          }
        });
      } catch (_) {}
    }
    try {
      if (config.value && typeof config.value === 'object') {
        config.value.enable_i18n = true;
        if (localeSource !== null) { config.value.locale_source = localeSource; }
      }
    } catch (_) {}
    state.patchedConfigs += 1;
    state.lastPatchAt = Date.now();
    return config;
  }

  function patchClient(client) {
    if (!client || (typeof client !== 'object' && typeof client !== 'function')) return;
    if (client.__codexZhI18nClient) return;
    var originalDynamic = client.getDynamicConfig;
    var originalLayer = client.getLayer;
    if (typeof originalDynamic !== 'function' && typeof originalLayer !== 'function') return;
    var patched = false;
    try {
      if (typeof originalDynamic === 'function') {
        Object.defineProperty(client, 'getDynamicConfig', {
          configurable: true,
          value: function (key) {
            var result = originalDynamic.apply(this, arguments);
            return String(key) === configId ? forceConfig(result) : result;
          }
        });
        patched = true;
      }
      if (typeof originalLayer === 'function') {
        Object.defineProperty(client, 'getLayer', {
          configurable: true,
          value: function (key) {
            var result = originalLayer.apply(this, arguments);
            return String(key) === configId ? forceConfig(result) : result;
          }
        });
        patched = true;
      }
      if (!patched) return;
      Object.defineProperty(client, '__codexZhI18nClient', { value: true, configurable: true });
      state.patchedClients += 1;
      state.lastPatchAt = Date.now();
    } catch (_) {}
  }

  function patchGlobal(statsig) {
    if (!statsig || typeof statsig !== 'object') return;
    patchClient(statsig);
    patchClient(statsig.firstInstance);
    patchClient(statsig.instance);
    var instances = statsig.instances;
    if (instances && typeof instances === 'object') {
      Object.keys(instances).forEach(function (key) { patchClient(instances[key]); });
    }
  }

  function installStatsigHook() {
    var patchCurrent = function () {
      try { patchGlobal(globalThis.__STATSIG__); } catch (_) {}
    };
    patchCurrent();
    var attempts = 0;
    var timer = window.setInterval(function () {
      patchCurrent();
      attempts += 1;
      if (attempts >= 400 || (state.patchedClients > 0 && state.patchedConfigs > 0)) {
        window.clearInterval(timer);
      }
    }, 50);
  }

  try {
    Object.defineProperty(Navigator.prototype, 'language', {
      configurable: true,
      get: function () { return locale; }
    });
    Object.defineProperty(Navigator.prototype, 'languages', {
      configurable: true,
      get: function () { return [locale, 'zh']; }
    });
  } catch (_) {}

  installStatsigHook();
  return JSON.stringify({
    status: state.patchedClients > 0 && state.patchedConfigs > 0 ? 'ok' : 'pending',
    configId: configId,
    enable_i18n: true,
    locale_source: localeSource,
    patchedClients: state.patchedClients,
    patchedConfigs: state.patchedConfigs,
    lastPatchAt: state.lastPatchAt
  });
})()
