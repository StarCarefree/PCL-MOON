(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const form = $('pack-form');
  const field = (name) => form.elements.namedItem(name);
  const textFields = ['id', 'name', 'description', 'version', 'minecraft', 'icon', 'coverUrl',
    'background', 'downloadUrl', 'sha256', 'fileName', 'shareUrl', 'sharePath'];
  // Session secrets and public address preferences intentionally live only in this closure.
  let token = '';
  let session = 0;
  let controller = null;
  let packs = [];
  let etag = '';
  let revision = '';
  let status = null;
  let busy = false;
  let editingId = null;
  let deletingPack = null;
  let linkPack = null;
  let publicOrigin = '';
  let formBaseline = '';

  class ApiError extends Error {
    constructor(message, code = 0) { super(message); this.code = code; }
  }

  function notice(id, message = '', kind = 'error') {
    const node = $(id);
    node.textContent = message;
    node.className = `notice ${kind}`;
    node.hidden = !message;
  }

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = String(text);
    return node;
  }

  function setBusy(value, label = '正在加载…') {
    busy = value;
    $('busy-label').hidden = !value;
    $('busy-label').textContent = label;
    $('catalog').setAttribute('aria-busy', String(value));
    $('pack-form').setAttribute('aria-busy', String(value));
    ['refresh', 'add', 'empty-action', 'editor-close', 'editor-cancel', 'delete-confirm',
      'delete-cancel', 'conflict-reload'].forEach((id) => { $(id).disabled = value; });
    $('save').disabled = value || !etag;
    $('save').textContent = value ? '处理中…' : '保存整合包';
    for (const input of form.querySelectorAll('input, textarea')) input.disabled = value;
    for (const button of $('pack-list').querySelectorAll('button')) button.disabled = value;
  }

  async function api(path, method = 'GET', payload, match) {
    if (!token || !controller) throw new ApiError('请先输入管理令牌连接服务。');
    const headers = { Accept: 'application/json', Authorization: `Bearer ${token}` };
    if (method !== 'GET') {
      if (!match) throw new ApiError('缺少目录版本，请先刷新后重试。');
      headers['If-Match'] = match;
      if (payload !== undefined) headers['Content-Type'] = 'application/json';
    }
    let response;
    try {
      response = await fetch(path, {
        method, headers, credentials: 'omit', cache: 'no-store', redirect: 'error',
        signal: AbortSignal.any([controller.signal, AbortSignal.timeout(20000)]),
        ...(payload === undefined ? {} : { body: JSON.stringify(payload) })
      });
    } catch (error) {
      if (error.name === 'AbortError') throw error;
      throw new ApiError(method === 'GET'
        ? '无法连接管理服务或请求超时，请检查网络后重试。'
        : '请求未完成，保存结果尚未确认。表单已保留，请先刷新目录核对，避免重复提交。');
    }
    let data;
    try { data = await response.json(); } catch {
      throw new ApiError(`服务器返回了无效的 JSON（HTTP ${response.status}）。`, response.status);
    }
    if (!response.ok) {
      const message = typeof data?.error === 'string' ? data.error : `请求失败（HTTP ${response.status}）。`;
      throw new ApiError(response.status === 401 || response.status === 403
        ? `${message} 请断开连接后重新输入管理令牌。` : message, response.status);
    }
    return { data, etag: response.headers.get('ETag') };
  }

  function applySnapshot(result) {
    const data = result.data;
    if (!data || !Array.isArray(data.packs) || !data.packs.every((pack) =>
      pack && typeof pack.id === 'string' && typeof pack.name === 'string')) {
      throw new ApiError('服务器目录格式无效，请检查服务版本。');
    }
    // ETag is authoritative: a Java long may exceed JS's safe integer range.
    let nextTag = result.etag;
    if (!/^"\d+"$/.test(nextTag || '')) {
      if (!Number.isSafeInteger(data.revision) || data.revision < 0) {
        throw new ApiError('服务器缺少有效的 ETag，无法安全写入目录。');
      }
      nextTag = `"${data.revision}"`;
    }
    etag = nextTag;
    revision = nextTag.slice(1, -1);
    packs = data.packs;
    render();
    $('editor-revision').textContent = `基于目录版本 ${revision}`;
    $('updated-at').textContent = `最近同步 ${new Date().toLocaleTimeString('zh-CN', { hour12: false })}`;
  }

  async function loadMetadata(ticket) {
    const [snapshot, service] = await Promise.all([api('/api/admin/packs'), api('/api/admin/status')]);
    if (ticket !== session) return;
    const nextStatus = service.data;
    if (!nextStatus || !Number.isInteger(nextStatus.publicPort) || nextStatus.publicPort < 1 || nextStatus.publicPort > 65535) {
      throw new ApiError('服务器未返回有效的公开端口。');
    }
    applySnapshot(snapshot);
    status = nextStatus;
    $('admin-port').textContent = String(status.adminPort ?? '—');
    $('public-port').textContent = String(status.publicPort);
    if (!publicOrigin) {
      const base = new URL(location.href);
      base.protocol = location.protocol === 'https:' ? 'https:' : 'http:';
      base.port = String(status.publicPort);
      publicOrigin = base.origin;
    }
  }

  function packButton(label, pack, handler, className = '') {
    const button = element('button', className, label);
    button.type = 'button';
    button.disabled = busy;
    button.setAttribute('aria-label', `${label}：${pack.name}`);
    button.addEventListener('click', () => handler(pack));
    return button;
  }

  function render() {
    const published = packs.filter((pack) => pack.enabled === true).length;
    $('metric-total').textContent = String(packs.length);
    $('metric-published').textContent = String(published);
    $('metric-draft').textContent = String(packs.length - published);
    $('metric-revision').textContent = revision || '—';
    const query = $('search').value.trim().toLocaleLowerCase();
    const filter = $('publish-filter').value;
    const visible = packs.filter((pack) => {
      const matches = [pack.id, pack.name, pack.version, pack.minecraft, pack.description]
        .some((value) => String(value ?? '').toLocaleLowerCase().includes(query));
      return matches && (filter === 'all' || (filter === 'published' ? pack.enabled === true : pack.enabled !== true));
    }).sort((a, b) => (Number(a.sortOrder) || 0) - (Number(b.sortOrder) || 0) || a.id.localeCompare(b.id));
    const list = $('pack-list');
    list.replaceChildren();
    for (const pack of visible) {
      const row = element('article', 'pack-row');
      row.setAttribute('role', 'listitem');
      const main = element('div', 'pack-main');
      const avatar = element('div', 'pack-avatar', Array.from(pack.name || pack.id)[0] || 'M');
      avatar.setAttribute('aria-hidden', 'true');
      const content = element('div', 'pack-text');
      const title = element('div', 'pack-title');
      title.append(element('h3', '', pack.name), element('span', 'version', pack.version || '未设置版本'));
      const meta = element('div', 'pack-meta');
      meta.append(element('span', 'code', pack.id), element('span', '', `Minecraft ${pack.minecraft || '未指定'}`),
        element('span', '', `排序 ${pack.sortOrder ?? 0}`), element('span', '', `${(pack.backupUrls || []).length} 个备用源`));
      const description = Array.from(String(pack.description || '').replace(/\s+/g, ' ').trim());
      const excerpt = description.length > 140 ? `${description.slice(0, 140).join('')}…` : description.join('');
      content.append(title, element('p', 'pack-description', excerpt || '暂无描述'), meta);
      main.append(avatar, content);
      const badge = element('span', pack.enabled === true ? 'badge' : 'badge draft', pack.enabled === true ? '● 已发布' : '○ 草稿');
      const actions = element('div', 'pack-actions');
      actions.append(packButton('编辑', pack, openEditor), packButton('链接', pack, openPublic), packButton('删除', pack, openDelete, 'danger quiet'));
      row.append(main, badge, actions);
      list.append(row);
    }
    $('empty-state').hidden = visible.length !== 0;
    const filtered = packs.length !== 0;
    $('empty-title').textContent = filtered ? '没有匹配的整合包' : '还没有整合包';
    $('empty-description').textContent = filtered ? '试试其他关键词，或清除发布状态筛选。' : '从第一个整合包开始构建你的目录。';
    $('empty-action').textContent = filtered ? '清除筛选' : '新建整合包';
    $('result-count').textContent = `显示 ${visible.length} / ${packs.length} 个整合包`;
  }

  function formSignature() {
    return JSON.stringify([...textFields.map((name) => field(name).value), field('backupUrls').value,
      field('enabled').checked, field('sortOrder').value]);
  }

  function openEditor(pack = null) {
    if (busy || !token) return;
    form.reset();
    editingId = pack ? pack.id : null;
    textFields.forEach((name) => { field(name).value = pack?.[name] ?? (name === 'fileName' ? 'pack.mrpack' : ''); });
    field('id').readOnly = !!pack;
    field('enabled').checked = pack?.enabled === true;
    field('sortOrder').value = String(pack?.sortOrder ?? 0);
    field('backupUrls').value = Array.isArray(pack?.backupUrls) ? pack.backupUrls.join('\n') : '';
    for (const input of form.querySelectorAll('input, textarea')) input.setCustomValidity('');
    $('editor-title').textContent = pack ? '编辑整合包' : '新建整合包';
    $('editor-revision').textContent = `基于目录版本 ${revision}`;
    notice('editor-notice');
    $('conflict-reload').hidden = true;
    formBaseline = formSignature();
    setBusy(false);
    $('editor').showModal();
    (pack ? field('name') : field('id')).focus();
  }

  function closeEditor() {
    if (busy) return;
    if (formSignature() !== formBaseline && !window.confirm('有尚未保存的修改，确定放弃吗？')) return;
    $('editor').close();
  }

  function openDelete(pack) {
    if (busy) return;
    deletingPack = pack;
    $('delete-description').textContent = `即将删除「${pack.name}」（${pack.id}）。`;
    notice('delete-notice');
    $('delete-dialog').showModal();
  }

  function isHttpUrl(value) {
    try {
      const url = new URL(value);
      return (url.protocol === 'https:' || url.protocol === 'http:') && !url.username && !url.password && !url.hash && url.port !== '0';
    } catch { return false; }
  }

  function readPack() {
    const data = {};
    textFields.forEach((name) => { data[name] = field(name).value.trim(); });
    if (editingId !== null) data.id = editingId;
    data.description = field('description').value;
    data.backupUrls = field('backupUrls').value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
    const invalidLine = data.backupUrls.findIndex((url) => url.length > 2048 || !isHttpUrl(url));
    field('backupUrls').setCustomValidity(data.backupUrls.length > 8 ? '最多填写 8 个备用下载地址。' :
      invalidLine < 0 ? '' : `第 ${invalidLine + 1} 个备用地址必须是不含凭据或片段的 HTTP(S) URL，且不超过 2048 字符。`);
    for (const name of ['downloadUrl', 'coverUrl', 'shareUrl', 'icon', 'background']) {
      field(name).setCustomValidity(data[name] && !isHttpUrl(data[name]) ? '请输入不含凭据或片段的 HTTP 或 HTTPS 地址。' : '');
    }
    for (const name of ['id', 'name', 'version', 'fileName']) {
      field(name).setCustomValidity(data[name] ? '' : '请填写此必填字段，不能仅包含空格。');
    }
    data.enabled = field('enabled').checked;
    data.sortOrder = Number(field('sortOrder').value);
    if (!form.reportValidity()) return null;
    return data;
  }

  async function recoverConflict(ticket, id, originalMessage) {
    etag = ''; // Never retry automatically or overwrite a stale snapshot.
    try {
      await loadMetadata(ticket);
      if (ticket !== session) return;
      const message = `${originalMessage}\n目录已重新加载到版本 ${revision}。你的输入已保留，请核对后再次保存；不会自动覆盖他人的修改。`;
      notice(id, message, 'warning');
      notice('global-notice', `发现版本冲突，目录已刷新至版本 ${revision}。请核对后重试。`, 'warning');
      if (id === 'editor-notice') $('conflict-reload').hidden = true;
      if (deletingPack && !packs.some((pack) => pack.id === deletingPack.id)) {
        $('delete-dialog').close();
        deletingPack = null;
        notice('global-notice', '该整合包已被其他管理员删除，目录已刷新。', 'warning');
      }
    } catch (error) {
      if (ticket !== session) return;
      notice(id, `${originalMessage}\n重新加载失败：${error.message} 输入仍已保留。`, 'error');
      if (id === 'editor-notice') $('conflict-reload').hidden = false;
    }
  }

  async function write(method, data, targetId, noticeId, onSuccess) {
    if (busy || !token) return;
    const ticket = session;
    setBusy(true, method === 'DELETE' ? '正在删除…' : '正在保存…');
    notice(noticeId);
    try {
      const path = targetId === null ? '/api/admin/packs' : `/api/admin/packs/${encodeURIComponent(targetId)}`;
      const result = await api(path, method, data, etag);
      if (ticket !== session) return;
      applySnapshot(result);
      onSuccess();
    } catch (error) {
      if (ticket !== session) return;
      if (error.code === 409 || error.code === 412) await recoverConflict(ticket, noticeId, error.message);
      else {
        notice(noticeId, error.message);
        if (noticeId === 'editor-notice') $('conflict-reload').hidden = false;
      }
    } finally {
      if (ticket === session) {
        setBusy(false);
        $('delete-confirm').disabled = !etag;
      }
    }
  }

  async function refresh(preserveEditor = false) {
    if (busy || !token) return;
    const ticket = session;
    setBusy(true);
    notice('global-notice');
    try {
      await loadMetadata(ticket);
      if (ticket !== session) return;
      notice('global-notice', '目录已同步。', 'success');
      if (preserveEditor) {
        notice('editor-notice', `目录已更新到版本 ${revision}，表单输入已保留。请核对后保存。`, 'warning');
        $('conflict-reload').hidden = true;
      }
    } catch (error) {
      if (ticket !== session) return;
      notice(preserveEditor ? 'editor-notice' : 'global-notice', `刷新失败，当前目录可能不是最新数据。${error.message}`);
    } finally { if (ticket === session) setBusy(false); }
  }

  function updatePublicLinks() {
    const input = $('public-origin');
    let base = null;
    try {
      base = new URL(input.value.trim());
      if (!isHttpUrl(input.value.trim()) || base.pathname !== '/' || base.search || base.hash) base = null;
    } catch { /* Native validation supplies feedback when copying. */ }
    input.setCustomValidity(base ? '' : '请输入 HTTP(S) 服务地址，仅包含主机和可选端口，不含路径、凭据或查询参数。');
    $('copy-catalog').disabled = !base;
    $('copy-download').disabled = !base;
    $('catalog-url').textContent = base ? `${base.origin}/index.json` : '请填写有效的公开服务地址';
    $('download-url').textContent = base && linkPack
      ? `${base.origin}/api/v1/packs/${encodeURIComponent(linkPack.id)}/download?mirror=${$('mirror-select').value}` : '';
    if (base) publicOrigin = base.origin;
  }

  function openPublic(pack = null) {
    if (!token) return;
    linkPack = pack;
    $('public-origin').value = publicOrigin;
    $('download-links').hidden = !pack;
    $('copy-download').hidden = !pack;
    const mirrors = $('mirror-select');
    mirrors.replaceChildren();
    if (pack) {
      const sources = [pack.downloadUrl, ...(Array.isArray(pack.backupUrls) ? pack.backupUrls : [])];
      sources.forEach((source, index) => {
        const option = element('option', '', index === 0 ? '主下载源 · mirror=0' : `备用源 ${index} · mirror=${index}`);
        option.value = String(index);
        option.title = String(source || '');
        mirrors.append(option);
      });
      $('download-note').textContent = pack.enabled === true
        ? `「${pack.name}」已发布。复制链接不会发起下载请求。` : `「${pack.name}」是草稿，公开下载入口不可用；发布后才可使用。`;
    }
    notice('public-notice');
    updatePublicLinks();
    $('public-dialog').showModal();
  }

  async function copyUrl(id) {
    updatePublicLinks();
    if (!$('public-origin').reportValidity()) return;
    const value = $(id).textContent;
    const ticket = session;
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable');
      await navigator.clipboard.writeText(value);
      if (ticket === session) notice('public-notice', '已复制到剪贴板。', 'success');
    } catch {
      if (ticket !== session) return;
      // On plain HTTP the Clipboard API may be unavailable. Select text for manual copy.
      const range = document.createRange();
      range.selectNodeContents($(id));
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
      notice('public-notice', '浏览器不允许自动复制，已选中链接，请按 Ctrl+C（或长按复制）。', 'warning');
    }
  }

  function disconnect() {
    session++;
    controller?.abort();
    controller = null;
    token = '';
    etag = '';
    revision = '';
    packs = [];
    status = null;
    publicOrigin = '';
    editingId = null;
    deletingPack = null;
    linkPack = null;
    for (const id of ['editor', 'delete-dialog', 'public-dialog']) if ($(id).open) $(id).close();
    form.reset();
    for (const input of form.querySelectorAll('input, textarea')) input.setCustomValidity('');
    field('id').readOnly = false;
    $('login-form').reset();
    $('search').value = '';
    $('publish-filter').value = 'all';
    $('public-origin').value = '';
    $('catalog-url').textContent = '';
    $('download-url').textContent = '';
    $('mirror-select').replaceChildren();
    $('delete-description').textContent = '';
    ['global-notice', 'login-error', 'editor-notice', 'delete-notice', 'public-notice'].forEach((id) => notice(id));
    render();
    setBusy(false);
    $('app').hidden = true;
    $('login-screen').hidden = false;
    $('login-button').disabled = false;
    $('login-button').textContent = '连接控制台 →';
    $('token').disabled = false;
    $('token').focus();
  }

  $('login-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    if (busy) return;
    token = $('token').value.trim();
    if (!token) { notice('login-error', '请输入有效的管理令牌。'); return; }
    const ticket = ++session;
    controller = new AbortController();
    $('token').value = '';
    $('token').disabled = true;
    $('login-button').disabled = true;
    $('login-button').textContent = '正在连接…';
    notice('login-error');
    setBusy(true);
    try {
      await loadMetadata(ticket);
      if (ticket !== session) return;
      $('login-screen').hidden = true;
      $('app').hidden = false;
      $('page-title').focus();
    } catch (error) {
      if (ticket !== session) return;
      token = '';
      controller.abort();
      controller = null;
      notice('login-error', error.message);
    } finally {
      if (ticket === session) {
        setBusy(false);
        $('token').disabled = false;
        $('login-button').disabled = false;
        $('login-button').textContent = '连接控制台 →';
        if (!$('login-screen').hidden) $('token').focus();
      }
    }
  });

  $('pack-form').addEventListener('submit', (event) => {
    event.preventDefault();
    const data = readPack();
    if (!data) return;
    const method = editingId === null ? 'POST' : 'PUT';
    write(method, data, editingId, 'editor-notice', () => {
      $('editor').close();
      notice('global-notice', `「${data.name}」已保存为${data.enabled ? '已发布' : '草稿'}，目录版本 ${revision}。`, 'success');
      $('page-title').focus();
    });
  });
  form.addEventListener('input', (event) => {
    if (typeof event.target.setCustomValidity === 'function') event.target.setCustomValidity('');
  });
  $('editor').addEventListener('cancel', (event) => { event.preventDefault(); closeEditor(); });
  $('editor').addEventListener('close', () => {
    form.reset();
    editingId = null;
    formBaseline = '';
  });
  $('delete-dialog').addEventListener('cancel', (event) => { if (busy) event.preventDefault(); });
  $('delete-dialog').addEventListener('close', () => { deletingPack = null; });
  $('delete-confirm').addEventListener('click', () => {
    if (!deletingPack) return;
    const pack = deletingPack;
    write('DELETE', undefined, pack.id, 'delete-notice', () => {
      $('delete-dialog').close();
      notice('global-notice', `「${pack.name}」已删除，目录版本 ${revision}。`, 'success');
      $('page-title').focus();
    });
  });
  $('delete-cancel').addEventListener('click', () => { if (!busy) $('delete-dialog').close(); });
  $('editor-close').addEventListener('click', closeEditor);
  $('editor-cancel').addEventListener('click', closeEditor);
  $('conflict-reload').addEventListener('click', () => refresh(true));
  $('disconnect').addEventListener('click', disconnect);
  $('refresh').addEventListener('click', () => refresh());
  $('add').addEventListener('click', () => openEditor());
  $('search').addEventListener('input', render);
  $('publish-filter').addEventListener('change', render);
  $('empty-action').addEventListener('click', () => {
    if (!packs.length) openEditor();
    else { $('search').value = ''; $('publish-filter').value = 'all'; render(); $('search').focus(); }
  });
  $('nav-packs').addEventListener('click', () => {
    $('search').value = ''; $('publish-filter').value = 'all'; render(); $('page-title').focus();
  });
  $('nav-public').addEventListener('click', () => openPublic());
  $('public-close').addEventListener('click', () => $('public-dialog').close());
  $('public-origin').addEventListener('input', () => { notice('public-notice'); updatePublicLinks(); });
  $('mirror-select').addEventListener('change', updatePublicLinks);
  $('copy-catalog').addEventListener('click', () => copyUrl('catalog-url'));
  $('copy-download').addEventListener('click', () => copyUrl('download-url'));
  // No bootstrap fetch: every request is gated behind explicit token submission.
  window.addEventListener('pagehide', disconnect);
})();
