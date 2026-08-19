(function () {
  "use strict";

  const POLL_MS = 2000;
  const state = {
    status: null,
    clients: [],
    events: [],
    selectedClientId: null,
    polling: false,
    abortController: null,
    traffic: [],
    destinations: [],
    alerts: [],
    telemetry: null,
    lastTrafficEventId: 0,
    expandedEvents: new Set(),
    bandBusy: false,
    lastStatusAt: null
  };

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));
  const text = (value, fallback = "—") => value === undefined || value === null || value === "" ? fallback : String(value);
  const first = (obj, keys, fallback) => {
    if (!obj) return fallback;
    for (const key of keys) {
      const value = key.split(".").reduce((current, part) => current && current[part], obj);
      if (value !== undefined && value !== null && value !== "") return value;
    }
    return fallback;
  };
  const bool = (value) => value === true || value === 1 || value === "true" || value === "True" || value === "online" || value === "active" || value === "running";
  const formatRate = (bytesPerSecond) => {
    const n = Number(bytesPerSecond);
    if (!Number.isFinite(n)) return "—";
    if (n < 1024) return `${Math.round(n)} B/s`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB/s`;
    return `${(n / (1024 * 1024)).toFixed(1)} MB/s`;
  };
  const formatBytes = (bytes) => {
    const n = Number(bytes);
    if (!Number.isFinite(n)) return "—";
    if (n < 1024) return `${Math.round(n)} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
    return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
  };
  const formatDate = (value, withDate = false) => {
    if (!value) return "—";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return text(value);
    return new Intl.DateTimeFormat("pl-PL", withDate ? { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" } : { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(date);
  };
  const escape = (value) => text(value).replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;" }[char]));
  const visibilitySourceLabel = (source) => ({ "ip-only": "tylko IP", "dns": "DNS", "dns-correlation": "korelacja DNS", "http-host": "HTTP Host", "tls-sni": "TLS SNI" }[String(source).toLowerCase()] || text(source, "tylko IP"));
  const api = async (url, options = {}) => {
    const response = await fetch(url, { headers: { Accept: "application/json", ...(options.headers || {}) }, ...options });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    if (response.status === 204) return {};
    const contentType = response.headers.get("content-type") || "";
    return contentType.includes("json") ? response.json() : {};
  };
  const optionalApi = async (url, options = {}) => {
    try { return await api(url, options); } catch (error) { return null; }
  };

  function setServiceState(kind, label) {
    const element = $("#serviceState");
    element.className = `service-state is-${kind}`;
    $(".status-dot", element).setAttribute("aria-hidden", "true");
    $(".status-dot", element).nextElementSibling.textContent = label;
  }

  function notice(message, kind = "warning") {
    const element = $("#globalNotice");
    element.textContent = message;
    element.className = `global-notice ${kind}`;
    element.hidden = !message;
  }

  function toast(message, kind = "ok") {
    const item = document.createElement("div");
    item.className = `toast ${kind === "error" ? "error" : ""}`;
    item.textContent = message;
    $("#toastRegion").append(item);
    window.setTimeout(() => item.remove(), 4200);
  }

  function statusValue(status, key, fallback) {
    return first(status, [key, `router.${key}`, `network.${key}`, `state.${key}`], fallback);
  }

  function normalizeStatus(data) {
    const routerRunning = bool(first(data, ["routerRunning", "running", "router.running", "hotspot.running", "isRunning"], false));
    const hotspot = first(data, ["hotspot", "network.hotspot"], {});
    const ethernet = first(data, ["ethernet", "network.ethernet", "uplink"], {});
    const share = first(data, ["share", "smb", "fileShare"], {});
    const gates = first(data, ["gates", "security.gates"], {});
    return {
      ...data,
      routerRunning,
      ssid: first(data, ["ssid", "hotspot.ssid", "settings.ssid"], first(hotspot, ["ssid", "name"], "WORK")),
      band: first(data, ["band", "hotspot.band", "settings.band"], first(hotspot, ["band"], "5GHz")),
      activeBand: first(data, ["activeBand", "hotspot.activeBand", "network.activeBand"], null),
      bandConfirmed: bool(first(data, ["bandConfirmed", "hotspot.bandConfirmed", "network.bandConfirmed"], false)),
      maxClients: first(data, ["maxClients", "hotspot.maxClients", "settings.maxClients"], 8),
      hotspotAddress: first(data, ["hotspotAddress", "workAddress", "gateway", "hotspot.gateway"], first(hotspot, ["address", "gateway"], "—")),
      ethernetOnline: bool(first(data, ["ethernetOnline", "ethernet.connected", "uplink.connected", "internet"], first(ethernet, ["online", "connected"], false))),
      ipv4Filtered: bool(first(data, ["ipv4Filtered", "filters.ipv4", "gates.ipv4"], first(gates, ["ipv4", "filtering"], false))),
      ipv6Blocked: bool(first(data, ["ipv6Blocked", "filters.ipv6", "gates.ipv6"], first(gates, ["ipv6", "ipv6Blocked"], false))),
      smbReady: bool(first(data, ["smbReady", "share.ready", "gates.smb"], first(share, ["ready", "active"], false))),
      sharePath: first(data, ["sharePath", "share.path", "smb.path"], first(share, ["path"], "\\\\WORK\\Firmowe")),
      shareAccount: first(data, ["shareAccount", "share.account", "smb.account"], first(share, ["account"], "workshare")),
      downloadRate: first(data, ["downloadRate", "traffic.downloadRate", "traffic.rxRate"], 0),
      uploadRate: first(data, ["uploadRate", "traffic.uploadRate", "traffic.txRate"], 0),
      totalBytes: first(data, ["totalBytes", "traffic.totalBytes", "traffic.bytes"], 0)
    };
  }

  function gateIcon(type) {
    const icons = { ethernet: "<path d=\"M4 8h16v8H4zM8 5v3m4-3v3m4-3v3M8 16v3m4-3v3m4-3v3\"/>", hotspot: "<path d=\"M4 9a12 12 0 0 1 16 0M7 12a7.5 7.5 0 0 1 10 0M10 15a3.5 3.5 0 0 1 4 0M12 19h.01\"/>", shield: "<path d=\"M12 3 19 6v5c0 4.3-2.7 8-7 10-4.3-2-7-5.7-7-10V6l7-3Z\"/><path d=\"m9 12 2 2 4-4\"/>", ipv6: "<circle cx=\"12\" cy=\"12\" r=\"8\"/><path d=\"M4 12h16M12 4a12 12 0 0 1 0 16M12 4a12 12 0 0 0 0 16\"/>", share: "<path d=\"M4 6h16v13H4zM8 6V4h8v2M8 10h8m-8 3h5\"/>" };
    return `<svg aria-hidden="true" viewBox="0 0 24 24">${icons[type] || icons.shield}</svg>`;
  }

  function renderGates(status) {
    const gates = [
      ["Połączenie Ethernet", status.ethernetOnline, status.ethernetOnline ? "Internet dostępny" : "Brak połączenia", first(status, ["ethernetAddress", "ethernet.ip", "uplink.address"], "Łącze nadrzędne · Ethernet"), "ethernet"],
      [`Hotspot ${status.ssid}`, status.routerRunning, status.routerRunning ? "Aktywny" : "Zatrzymany", status.routerRunning ? `${status.ssid} · ${status.hotspotAddress}` : "Uruchom, aby odizolować ruch", "hotspot"],
      ["Filtracja IPv4", status.ipv4Filtered, status.ipv4Filtered ? "Aktywna" : "Nieaktywna", "WORK → prywatne zakresy zablokowane", "shield"],
      ["Blokada IPv6", status.ipv6Blocked, status.ipv6Blocked ? "Aktywna" : "Nieaktywna", "Brak alternatywnej drogi do LAN-u", "ipv6"],
      ["Udział SMB Firmowe", status.smbReady, status.smbReady ? "Gotowy" : "Niedostępny", status.smbReady ? status.sharePath : "Skonfiguruj udział, aby kontynuować", "share"]
    ];
    $("#gateList").innerHTML = gates.map(([name, ok, label, detail, icon]) => `<div class="gate-row ${ok ? "is-ok" : "is-error"}"><span class="gate-name"><span class="gate-icon">${gateIcon(icon)}</span>${escape(name)}</span><span class="gate-state">${escape(label)}</span><span class="gate-detail">${escape(detail)}</span></div>`).join("");
    const passing = gates.filter((gate) => gate[1]).length;
    $("#gateSummary").textContent = `${passing}/${gates.length} bramek aktywnych`;
  }

  function renderOverview(status) {
    const running = status.routerRunning;
    const toggle = $("#routerToggle");
    toggle.disabled = false;
    toggle.setAttribute("aria-checked", String(running));
    $("#routerControlHint").textContent = running ? `Aktywny · ${status.hotspotAddress}` : "Router zatrzymany";
    $("#routerSsidLabel").textContent = status.ssid;
    $("#wifiState").textContent = running ? "aktywne" : "nieaktywne";
    const wifiForm = $("#wifiForm");
    if (wifiForm.dataset.dirty !== "true") {
      $("#ssid").value = status.ssid;
      setBandValue(status.band);
      const configuredBandLabel = status.band === "2.4GHz" ? "2,4 GHz" : "5 GHz";
      const activeBandLabel = status.activeBand === "2.4GHz" ? "2,4 GHz" : status.activeBand === "5GHz" ? "5 GHz" : "nieznane";
      $("#bandState").textContent = status.routerRunning ? (status.bandConfirmed ? `Aktywne: ${activeBandLabel} · zmiana wykona kontrolowany restart.` : "Aktywne pasmo niepotwierdzone · sprawdź stan routera.") : `Ustawione: ${configuredBandLabel} · zostanie użyte przy uruchomieniu.`;
      $("#maxClients").value = String(status.maxClients);
      if (typeof status.wifiPassword === "string") $("#wifiPassword").value = status.wifiPassword;
    }
    $("#sharePath").textContent = status.sharePath;
    $("#shareAccount").textContent = status.shareAccount;
    $("#shareState").textContent = status.smbReady ? "gotowy" : "niedostępny";
    $("#shareState").className = `state-pill ${status.smbReady ? "state-ok" : "state-error"}`;
    $("#trafficTotal").textContent = formatBytes(status.totalBytes);
    $("#downloadRate").textContent = formatRate(status.downloadRate);
    $("#uploadRate").textContent = formatRate(status.uploadRate);
    $("#trafficWindow").textContent = status.trafficEstimated ? "szacunek interfejsu" : "ostatnie 60 s";
    renderGates(status);
  }

  function normalizeClients(data) {
    const list = Array.isArray(data) ? data : first(data, ["clients", "items", "devices"], []);
    return Array.isArray(list) ? list.map((client, index) => ({
      ...client,
      id: text(first(client, ["id", "mac", "address"], `client-${index}`)),
      name: first(client, ["name", "hostname", "hostName", "deviceName"], "Nieznane urządzenie"),
      ip: first(client, ["ip", "ipAddress", "address"], "—"),
      mac: first(client, ["mac", "macAddress"], "—"),
      connectedAt: first(client, ["connectedAt", "connectedSince", "since"], null),
      rx: first(client, ["downloadBytes", "rxBytes", "receivedBytes", "traffic.download"], 0),
      tx: first(client, ["uploadBytes", "txBytes", "sentBytes", "traffic.upload"], 0),
      downloadRate: first(client, ["downloadRate", "rxRate", "traffic.downloadRate"], 0),
      uploadRate: first(client, ["uploadRate", "txRate", "traffic.uploadRate"], 0),
      estimated: Boolean(first(client, ["isEstimated", "estimated"], false))
    })) : [];
  }

  function deviceIcon(name) { return /phone|android|iphone|telefon/i.test(name) ? "⌁" : /tablet|ipad/i.test(name) ? "▣" : "▱"; }
  function renderClients(list) {
    state.clients = list;
    const clientFilter = $("#trafficClientFilter");
    const previousFilter = clientFilter.value;
    clientFilter.innerHTML = `<option value="all">Wszyscy klienci</option>${list.map((client) => `<option value="${escape(client.id)}">${escape(client.name)}</option>`).join("")}`;
    clientFilter.value = list.some((client) => client.id === previousFilter) ? previousFilter : "all";
    $("#clientCount").textContent = `${list.length} ${list.length === 1 ? "urządzenie" : "urządzeń"}`;
    if (!list.length) {
      const ssid = state.status?.ssid || "WORK";
      $("#clientsBody").innerHTML = `<tr class="empty-row"><td colspan="5"><span class="empty-icon">⌁</span><strong>Brak podłączonych urządzeń</strong><span>Połącz laptop z siecią ${escape(ssid)}, aby pojawił się tutaj.</span></td></tr>`;
      renderSession(null);
      return;
    }
    if (!state.selectedClientId || !list.some((client) => client.id === state.selectedClientId)) state.selectedClientId = list[0].id;
    $("#clientsBody").innerHTML = list.map((client) => {
      const selected = client.id === state.selectedClientId;
      return `<tr class="${selected ? "client-selected" : ""}"><td><div class="device-cell"><span class="device-icon" aria-hidden="true">${deviceIcon(client.name)}</span><span><strong>${escape(client.name)}</strong><small>${escape(client.mac)}</small></span></div></td><td>${escape(client.ip)}</td><td>${escape(formatDate(client.connectedAt, true))}</td><td>${escape(formatBytes(Number(client.rx) + Number(client.tx)))}</td><td><button class="select-client" type="button" data-client-id="${escape(client.id)}" aria-pressed="${selected}">${selected ? "wybrany" : "wybierz"}</button></td></tr>`;
    }).join("");
    $$(`[data-client-id]`, $("#clientsBody")).forEach((button) => button.addEventListener("click", () => selectPrimaryClient(button)));
    renderSession(list.find((client) => client.id === state.selectedClientId) || list[0]);
  }

  async function selectPrimaryClient(button) {
    if (button.getAttribute("aria-pressed") === "true") return;
    const client = state.clients.find((item) => item.id === button.dataset.clientId);
    if (!client) return;
    button.disabled = true;
    try {
      await api("/api/clients/primary", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ macAddress: client.mac }) });
      state.selectedClientId = client.id;
      renderClients(state.clients);
      toast(`Wybrano ${client.name}.`);
    } catch (error) {
      button.disabled = false;
      toast("Nie udało się wybrać klienta.", "error");
    }
  }

  function renderSession(client) {
    if (!client) {
      $("#sessionLabel").textContent = "brak połączenia";
      $("#sessionClient").innerHTML = `<span class="client-avatar" aria-hidden="true">—</span><div><strong>Nie wybrano laptopa</strong><span>Oznacz urządzenie w tabeli poniżej</span></div>`;
      $("#sessionSince").textContent = "—"; $("#sessionDownload").textContent = "—"; $("#sessionUpload").textContent = "—";
      return;
    }
    $("#sessionLabel").textContent = client.estimated ? "szacunek całego hotspotu" : "aktywny klient";
    $("#sessionClient").innerHTML = `<span class="client-avatar" aria-hidden="true">${escape(deviceIcon(client.name))}</span><div><strong>${escape(client.name)}</strong><span>${escape(client.ip)} · ${escape(client.mac)}</span></div>`;
    $("#sessionSince").textContent = formatDate(client.connectedAt, true);
    $("#sessionDownload").textContent = formatBytes(client.rx);
    $("#sessionUpload").textContent = formatBytes(client.tx);
  }

  function renderTraffic(status) {
    const current = Math.max(Number(status.downloadRate) || 0, Number(status.uploadRate) || 0);
    state.traffic.push({ download: Number(status.downloadRate) || 0, upload: Number(status.uploadRate) || 0 });
    if (state.traffic.length > 36) state.traffic.shift();
    const max = Math.max(...state.traffic.map((entry) => Math.max(entry.download, entry.upload)), 1);
    if (!current) { $("#trafficChart").innerHTML = `<span class="chart-empty">Brak danych transferu</span>`; return; }
    $("#trafficChart").innerHTML = state.traffic.map((entry) => `<span class="traffic-bar" style="--bar-height:${Math.max(4, (entry.download / max) * 100)}%" title="Pobieranie: ${escape(formatRate(entry.download))}"></span><span class="traffic-bar upload" style="--bar-height:${Math.max(2, (entry.upload / max) * 70)}%" title="Wysyłanie: ${escape(formatRate(entry.upload))}"></span>`).join("");
  }

  const normalizeRisk = (value) => {
    const risk = String(value || "").toLowerCase();
    if (risk.includes("high") || risk.includes("critical") || risk.includes("error")) return "high";
    if (risk.includes("medium") || risk.includes("warn")) return "medium";
    if (risk.includes("low") || risk.includes("info")) return "low";
    return "none";
  };
  const riskLabel = (risk) => ({ high: "wysokie", medium: "średnie", low: "niskie", none: "brak alertu" }[risk] || "brak alertu");
  const eventDestination = (event) => first(event, ["domain", "host", "sni", "destination", "ipAddress"], "Nieznany cel");
  const eventKey = (event, index) => String(first(event, ["id", "eventId"], "") || `${first(event, ["timestamp", "at", "time"], "brak-czasu")}|${eventDestination(event)}|${index}`);
  const visibilityConfidence = (value) => {
    const numeric = Number(value);
    if (Number.isFinite(numeric) && numeric > 0 && numeric <= 1) return `${Math.round(numeric * 100)}%`;
    if (Number.isFinite(numeric) && numeric > 1) return `${Math.round(numeric)}%`;
    return text(value, "brak danych");
  };

  function normalizeEvents(data) {
    const list = Array.isArray(data) ? data : first(data, ["events", "items", "entries"], []);
    return Array.isArray(list) ? list.slice(0, 12).map((event, index) => {
      const level = String(first(event, ["level", "severity", "kind"], "info")).toLowerCase();
      return {
        id: eventKey(event, index),
        at: first(event, ["at", "timestamp", "createdAt", "time"], null),
        level,
        client: first(event, ["client", "device", "hostname", "hostName"], "Usługa WorkRouter"),
        destination: eventDestination(event),
        protocol: first(event, ["protocol", "transport"], "systemowe"),
        risk: normalizeRisk(level),
        message: first(event, ["message", "text", "description"], "Zdarzenie systemowe"),
        details: {
          direction: first(event, ["direction"], "—"),
          source: first(event, ["source"], "—"),
          ports: first(event, ["sourcePort", "destinationPort"], null) ? `${text(first(event, ["sourcePort"], "—"))} → ${text(first(event, ["destinationPort"], "—"))}` : "—",
          bytes: first(event, ["bytes", "totalBytes"], null),
          visibilitySource: first(event, ["visibilitySource"], "—"),
          visibilityConfidence: first(event, ["visibilityConfidence"], null),
          note: first(event, ["note"], "")
        }
      };
    }) : [];
  }

  function normalizeConnectionEvents(data, alerts = []) {
    const list = Array.isArray(data) ? data : first(data, ["events", "items", "entries"], []);
    if (!Array.isArray(list)) return [];
    return list.slice(0, 20).map((event, index) => {
      const destination = eventDestination(event);
      const client = first(event, ["client", "device", "hostname", "hostName"], "Nieznane urządzenie");
      const matchingAlert = (Array.isArray(alerts) ? alerts : []).find((alert) => {
        const alertDestination = String(first(alert, ["destination", "ipAddress"], ""));
        const alertClient = String(first(alert, ["client"], ""));
        return alertDestination && (destination.includes(alertDestination) || alertDestination.includes(destination)) && (!alertClient || alertClient === client);
      });
      const risk = normalizeRisk(matchingAlert && first(matchingAlert, ["severity", "risk", "level"], ""));
      return {
        id: eventKey(event, index),
        at: first(event, ["timestamp", "at", "time"], null),
        level: risk === "high" ? "error" : risk === "medium" ? "warn" : "ok",
        client,
        destination,
        protocol: first(event, ["protocol", "transport"], "—"),
        risk,
        message: `${client} · ${destination}`,
        details: {
          direction: first(event, ["direction"], "—"),
          source: first(event, ["source", "ipAddress"], "—"),
          ports: `${text(first(event, ["sourcePort"], "—"))} → ${text(first(event, ["destinationPort"], "—"))}`,
          bytes: first(event, ["bytes"], null),
          visibilitySource: first(event, ["visibilitySource"], "ip-only"),
          visibilityConfidence: first(event, ["visibilityConfidence"], null),
          note: first(event, ["note"], matchingAlert ? first(matchingAlert, ["message", "detail"], "") : "")
        }
      };
    });
  }

  function renderEvents(events) {
    state.events = events;
    const currentKeys = new Set(events.map((event) => event.id));
    state.expandedEvents = new Set([...state.expandedEvents].filter((key) => currentKeys.has(key)));
    if (!events.length) { $("#eventList").innerHTML = `<li class="event-empty">Brak zdarzeń do wyświetlenia.</li>`; return; }
    $("#eventList").innerHTML = events.map((event) => {
      const expanded = state.expandedEvents.has(event.id);
      const detailId = `event-detail-${encodeURIComponent(event.id)}`;
      const riskClass = event.risk === "none" ? "none" : event.risk;
      const detail = event.details || {};
      return `<li class="event-item is-${event.level.includes("error") || event.level.includes("fail") ? "error" : event.level.includes("warn") ? "warn" : "ok"}${expanded ? " is-expanded" : ""}"><button class="event-toggle" type="button" data-event-id="${escape(event.id)}" aria-expanded="${expanded}" aria-controls="${escape(detailId)}"><time class="event-time">${escape(formatDate(event.at, false))}</time><span class="event-point" aria-hidden="true"></span><span class="event-summary"><strong>${escape(event.client)}</strong><span>${escape(event.destination)}</span><span>${escape(event.protocol)}</span></span><span class="event-risk ${riskClass}">${escape(riskLabel(event.risk))}</span><span class="event-chevron" aria-hidden="true">⌄</span></button><div class="event-detail" id="${escape(detailId)}" aria-hidden="${!expanded}"><div class="event-detail-inner"><dl><div><dt>Kierunek</dt><dd>${escape(detail.direction)}</dd></div><div><dt>Źródło</dt><dd>${escape(detail.source)}</dd></div><div><dt>Porty</dt><dd>${escape(detail.ports)}</dd></div><div><dt>Bajty próbki</dt><dd>${escape(detail.bytes === null || detail.bytes === undefined ? "—" : formatBytes(detail.bytes))}</dd></div><div><dt>Nazwa z</dt><dd>${escape(visibilitySourceLabel(detail.visibilitySource))}</dd></div><div><dt>Pewność</dt><dd>${escape(visibilityConfidence(detail.visibilityConfidence))}</dd></div></dl><p>${escape(detail.note || "Tylko metadane połączenia; brak payloadu, pełnego URL-a i treści.")}</p></div></div></li>`;
    }).join("");
  }

  function toggleEvent(event) {
    const button = event.target.closest(".event-toggle");
    if (!button) return;
    const key = button.dataset.eventId;
    const row = button.closest(".event-item");
    const detail = row && row.querySelector(".event-detail");
    const expanded = state.expandedEvents.has(key);
    if (expanded) state.expandedEvents.delete(key); else state.expandedEvents.add(key);
    button.setAttribute("aria-expanded", String(!expanded));
    row.classList.toggle("is-expanded", !expanded);
    if (detail) detail.setAttribute("aria-hidden", String(expanded));
  }

  function normalizeDestinations(status) {
    const analytics = first(status, ["traffic", "analytics", "inspection"], {});
    const list = first(status, ["destinations", "traffic.destinations", "analytics.destinations", "inspection.destinations"], first(analytics, ["destinations", "topDestinations"], []));
    return Array.isArray(list) ? list.map((item) => ({
      hostname: first(item, ["hostname", "hostName", "domain", "host"], first(item, ["key"], null)),
      ipPort: first(item, ["ipPort", "destination", "address", "ip"], first(item, ["key"], "—")),
      protocol: first(item, ["protocol", "transport"], "—"),
      queries: first(item, ["queries", "queryCount", "count"], 0),
      bytes: first(item, ["bytes", "totalBytes"], 0),
      clients: first(item, ["clients", "clientCount"], 0),
      clientIds: first(item, ["clientIds", "clientMacs", "macAddresses"], []),
      risk: String(first(item, ["risk", "riskLevel", "severity"], "low")).toLowerCase(),
      source: first(item, ["source", "nameSource", "resolutionSource", "visibilitySource"], "tylko IP"),
      confidence: first(item, ["confidence", "nameConfidence", "visibilityConfidence"], "niska")
    })) : [];
  }

  function normalizeAlerts(status) {
    const analytics = first(status, ["traffic", "analytics", "inspection"], {});
    const list = first(status, ["alerts", "traffic.alerts", "analytics.alerts", "inspection.alerts"], first(analytics, ["alerts"], []));
    return Array.isArray(list) ? list.map((item) => ({
      level: String(first(item, ["level", "risk", "severity"], "low")).toLowerCase(),
      title: first(item, ["title", "name", "code"], "Sygnał ruchu"),
      detail: first(item, ["detail", "message", "description"], "Wymaga ręcznej weryfikacji."),
      at: first(item, ["at", "timestamp", "createdAt"], null),
      destination: first(item, ["destination", "client"], "")
    })) : [];
  }

  function renderOverviewAnalytics(destinations, alerts) {
    const alertHost = $("#overviewAlerts");
    alertHost.innerHTML = alerts.length ? alerts.slice(0, 3).map((alert) => `<div class="summary-item is-${escape(alert.level)}"><strong>${escape(alert.title)}</strong><span>${escape(formatDate(alert.at))}</span></div>`).join("") : `<div class="summary-empty">Brak nowych sygnałów.</div>`;
    const destinationHost = $("#overviewDestinations");
    destinationHost.innerHTML = destinations.length ? destinations.slice(0, 3).map((item) => `<div class="summary-item"><strong>${escape(item.hostname || item.ipPort)}</strong><span>${escape(formatBytes(item.bytes))}</span></div>`).join("") : `<div class="summary-empty">Analiza celów niedostępna.</div>`;
  }

  function renderTimeline(status) {
    const analytics = first(status, ["traffic", "analytics", "inspection"], {});
    const points = first(status, ["timeline", "traffic.timeline", "analytics.timeline", "inspection.timeline"], first(analytics, ["timeline", "series"], []));
    const timeline = $("#trafficTimeline");
    if (!Array.isArray(points) || !points.length) {
      timeline.innerHTML = `<text x="360" y="94" text-anchor="middle">Brak danych inspekcji ruchu</text>`;
      $("#timelineLegend").textContent = "brak danych"; $("#timelineFrom").textContent = "—"; $("#timelineTo").textContent = "—";
      return;
    }
    const values = points.map((point) => Number(first(point, ["bytes", "value", "download", "total"], 0)) || 0);
    const max = Math.max(...values, 1); const left = 18; const width = 684; const bottom = 155; const top = 18;
    const coords = values.map((value, index) => `${left + (index / Math.max(values.length - 1, 1)) * width},${bottom - (value / max) * (bottom - top)}`);
    const area = `${left},${bottom} ${coords.join(" ")} ${left + width},${bottom}`;
    timeline.innerHTML = `<path class="timeline-area" d="M ${area} Z"></path><polyline class="timeline-line" points="${coords.join(" ")}"></polyline><line class="timeline-grid" x1="${left}" y1="${bottom}" x2="${left + width}" y2="${bottom}"></line>`;
    $("#timelineLegend").textContent = `${points.length} punktów · ${formatBytes(values.reduce((sum, value) => sum + value, 0))}`;
    $("#timelineFrom").textContent = formatDate(first(points[0], ["at", "timestamp", "time"], null), true); $("#timelineTo").textContent = formatDate(first(points[points.length - 1], ["at", "timestamp", "time"], null), true);
  }

  function renderDestinations(destinations) {
    state.destinations = destinations;
    $("#destinationCount").textContent = `${destinations.length} ${destinations.length === 1 ? "wpis" : "wpisów"}`;
    if (!destinations.length) { $("#destinationsBody").innerHTML = `<tr class="empty-row"><td colspan="9"><strong>Brak danych inspekcji ruchu</strong><span>Usługa nie zgłosiła domen ani celów połączeń.</span></td></tr>`; $("#trafficAvailability").textContent = "brak danych inspekcji"; $("#limitedVisibilityCount").textContent = "Ograniczona widoczność: brak próbek"; return; }
    $("#trafficAvailability").textContent = "metadane transportu";
    const limited = destinations.filter((item) => item.source === "tylko IP" || item.source === "ip-only" || /doh|ech|quic|vpn/i.test(item.source)).length;
    $("#limitedVisibilityCount").textContent = `Ograniczona widoczność: ${limited}/${destinations.length} wpisów`;
    $("#destinationsBody").innerHTML = destinations.map((item) => `<tr><td><strong>${escape(item.hostname || "Nieznany host")}</strong></td><td><span class="source-chip">${escape(visibilitySourceLabel(item.source))}</span></td><td><span class="confidence">${escape(item.confidence)}</span></td><td>${escape(item.ipPort)}</td><td>${escape(item.protocol)}</td><td>${escape(item.queries)}</td><td>${escape(formatBytes(item.bytes))}</td><td>${escape(item.clients)}</td><td><span class="destination-risk ${escape(item.risk)}">${escape(item.risk)}</span></td></tr>`).join("");
  }

  function renderAlerts(alerts) {
    state.alerts = alerts;
    $("#alertCount").textContent = `${alerts.length} ${alerts.length === 1 ? "sygnał" : "sygnałów"}`;
    $("#alertsList").innerHTML = alerts.length ? alerts.map((alert) => `<div class="alert-row ${escape(alert.level)}"><span class="alert-point" aria-hidden="true"></span><div><strong>${escape(alert.title)}</strong><small>${escape(alert.detail)}</small></div><time>${escape(formatDate(alert.at))}</time></div>`).join("") : `<div class="empty-state"><strong>Brak alertów</strong><span>Heurystyki pojawią się po udostępnieniu danych ruchu.</span></div>`;
  }

  function renderRules(status) {
    const rules = first(status, ["rules", "security.rules"], null);
    if (!Array.isArray(rules) || !rules.length) return;
    $("#rulesList").innerHTML = rules.map((rule) => { const active = bool(first(rule, ["active", "enabled", "pass"], false)); return `<div class="rule-row"><span class="rule-indicator ${active ? "is-on" : ""}"></span><div><strong>${escape(first(rule, ["label", "name"], "Reguła"))}</strong><small>${escape(first(rule, ["detail", "description"], ""))}</small></div><em>${active ? "aktywna" : "nieaktywna"}</em></div>`; }).join("");
  }

  function renderSettingsCapabilities(status) {
    const settings = first(status, ["settings", "configuration"], {});
    const capabilities = first(status, ["capabilities", "settingsCapabilities"], {});
    const supported = bool(first(capabilities, ["settingsWrite", "operationalSettings"], false));
    ["openPanelAtLogin", "autoStartRouter", "trafficInspectionEnabled", "retentionHours", "clearHistory"].forEach((id) => { const control = document.getElementById(id); if (control) control.disabled = !supported || (id === "clearHistory" && !bool(first(capabilities, ["clearHistory"], false))); });
    if (!supported) { $("#settingsAvailability").textContent = "nieudostępnione przez usługę"; return; }
    $("#settingsAvailability").textContent = "dostępne"; $("#settingsHint").textContent = "Zmiany są lokalne dla WorkRouter i nie wpływają na politykę laptopa firmowego.";
    $("#openPanelAtLogin").checked = bool(first(status, ["openPanelAtLogin", "settings.openPanelAtLogin"], first(settings, ["openPanelAtLogin"], false)));
    $("#autoStartRouter").checked = bool(first(status, ["autoStartRouter", "settings.autoStartRouter"], first(settings, ["autoStartRouter"], false)));
    $("#trafficInspectionEnabled").checked = bool(first(status, ["trafficInspectionEnabled", "settings.trafficInspectionEnabled"], first(settings, ["trafficInspectionEnabled"], false)));
    const retention = first(status, ["retentionHours", "settings.retentionHours"], first(settings, ["retentionHours"], 24)); $("#retentionHours").value = String(retention);
  }

  async function saveOperationalSettings() {
    const statusLabel = $("#settingsAvailability");
    try {
      const response = await api("/api/preferences", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ openPanelAtLogin: $("#openPanelAtLogin").checked, autoStartRouter: $("#autoStartRouter").checked, trafficInspectionEnabled: $("#trafficInspectionEnabled").checked, retentionHours: Number($("#retentionHours").value) }) });
      if (bool(first(response, ["requiresRouterRestart", "preferences.requiresRouterRestart"], false))) { statusLabel.textContent = "zapisano · wymaga restartu routera"; toast("Zapisano. Zmiana inspekcji zacznie działać po restarcie routera."); } else { statusLabel.textContent = "zapisano"; toast("Ustawienia operacyjne zapisane."); }
    } catch (error) { statusLabel.textContent = "błąd zapisu"; toast("Nie udało się zapisać ustawień operacyjnych.", "error"); }
  }

  async function clearHistory() {
    openConfirm("Wyczyścić historię?", "Zagregowane dane ruchu i alertów zostaną usunięte zgodnie z polityką retencji.", async () => {
      try { await api("/api/traffic/clear", { method: "POST" }); toast("Historia wyczyszczona."); if (state.status) await poll(); }
      catch (error) { toast("Nie udało się wyczyścić historii.", "error"); }
    });
  }

  function renderAnalytics(status) {
    const riskFilter = $("#trafficRiskFilter").value; const clientFilter = $("#trafficClientFilter").value;
    const allDestinations = normalizeDestinations(status); const destinations = allDestinations.filter((item) => (riskFilter === "all" || item.risk === riskFilter) && (clientFilter === "all" || (Array.isArray(item.clientIds) && item.clientIds.includes(clientFilter)))); const alerts = normalizeAlerts(status);
    renderOverviewAnalytics(destinations, alerts); renderTimeline(status); renderDestinations(destinations); renderAlerts(alerts); renderRules(status); renderSettingsCapabilities(status);
  }

  function renderPreferences(preferences) {
    if (!preferences) { $("#settingsAvailability").textContent = "preferencje niedostępne"; return; }
    $("#settingsAvailability").textContent = "dostępne"; $("#settingsHint").textContent = "Zmiany dotyczą tylko WorkRouter. Otwieranie panelu nie uruchamia hotspotu.";
    ["openPanelAtLogin", "autoStartRouter", "trafficInspectionEnabled", "retentionHours"].forEach((id) => { const control = document.getElementById(id); control.disabled = false; });
    $("#clearHistory").disabled = false;
    $("#openPanelAtLogin").checked = bool(preferences.openPanelAtLogin); $("#autoStartRouter").checked = bool(preferences.autoStartRouter); $("#trafficInspectionEnabled").checked = bool(preferences.trafficInspectionEnabled); $("#retentionHours").value = String(preferences.retentionHours || 24);
  }

  function telemetryFrom(summary, events, captureStatus) {
    if (!summary) return null;
    const result = { ...summary, events: Array.isArray(events) ? events : [], captureStatus: captureStatus || {} };
    const eventByDestination = new Map(result.events.filter((item) => item && item.destination).map((item) => [String(item.destination), item]));
    result.destinations = Array.isArray(summary.destinations) ? summary.destinations.map((item) => { const event = eventByDestination.get(String(first(item, ["key"], ""))); return event ? { ...item, visibilitySource: event.visibilitySource, visibilityConfidence: event.visibilityConfidence, hostname: event.domain || event.host || event.sni, ipPort: event.destination } : item; }) : [];
    result.alerts = Array.isArray(summary.alerts) ? summary.alerts : [];
    return result;
  }

  function renderTelemetry(summary, events, captureStatus) {
    const telemetry = telemetryFrom(summary, events, captureStatus); state.telemetry = telemetry;
    if (!telemetry) { $("#trafficAvailability").textContent = "inspekcja niedostępna"; $("#settingsAvailability").textContent = "preferencje niedostępne"; return; }
    renderOverviewAnalytics(normalizeDestinations(telemetry), normalizeAlerts(telemetry)); renderTimeline(telemetry); renderDestinations(normalizeDestinations(telemetry)); renderAlerts(normalizeAlerts(telemetry));
    const limited = Number(telemetry.encryptedOrUnknownCount || 0); $("#limitedVisibilityCount").textContent = `Ograniczona widoczność: ${limited} wpisów zaszyfrowanych/nieznanych · DoH-like ${Number(telemetry.doHLikeCount || 0)} · DoT ${Number(telemetry.doTCount || 0)} · QUIC ${Number(telemetry.quicCount || 0)} · VPN-like ${Number(telemetry.vpnLikeCount || 0)}`;
    $("#pauseControlState").textContent = telemetry.pauseControlSupported ? "Pauzowanie przechwytywania: dostępne." : "Pauzowanie przechwytywania: niedostępne (brak obsługi w usłudze).";
    if (Array.isArray(telemetry.limitations) && telemetry.limitations.length) $("#rulesHint").textContent = telemetry.limitations.join(" ");
    const detail = first(telemetry.captureStatus, ["detail"], first(telemetry, ["detail"], ""));
    $("#trafficAvailability").textContent = telemetry.enabled ? (detail || `${telemetry.running ? "tryb wydajny · metadane przepływów" : "inspekcja zatrzymana"}${telemetry.volatile ? " · sesja ulotna" : ""}`) : "inspekcja wyłączona";
  }

  async function poll() {
    if (state.polling) return;
    state.polling = true;
    if (state.abortController) state.abortController.abort();
    state.abortController = new AbortController();
    const timer = window.setTimeout(() => state.abortController.abort(), 6000);
    try {
      const signal = state.abortController.signal;
      const trafficWindow = { "1h": 60, "24h": 1440, "7d": 10080 }[$("#trafficTimeFilter").value] || 1440;
      const [status, clients, events, preferences, trafficSummary, trafficEvents] = await Promise.all([api("/api/status", { signal }), api("/api/clients", { signal }), api("/api/events", { signal }), optionalApi("/api/preferences", { signal }), optionalApi(`/api/traffic/summary?windowMinutes=${trafficWindow}`, { signal }), optionalApi(`/api/traffic/events?afterId=${state.lastTrafficEventId}`, { signal })]);
      state.status = normalizeStatus(status || {});
      state.lastStatusAt = new Date();
      const connectionEvents = normalizeConnectionEvents(trafficEvents && trafficEvents.events || trafficSummary && trafficSummary.timeline, trafficSummary && trafficSummary.alerts);
      renderOverview(state.status); renderTraffic(state.status); renderClients(normalizeClients(clients)); renderEvents(connectionEvents.length ? connectionEvents : normalizeEvents(events)); renderAnalytics(state.status); renderPreferences(preferences); renderTelemetry(trafficSummary, trafficEvents && trafficEvents.events, trafficEvents && trafficEvents.status);
      if (trafficEvents && Array.isArray(trafficEvents.events) && trafficEvents.events.length) state.lastTrafficEventId = Math.max(state.lastTrafficEventId, ...trafficEvents.events.map((item) => Number(item.id) || 0));
      setServiceState("online", "Usługa działa"); notice("");
      $("#lastUpdated").textContent = `Ostatnia aktualizacja ${formatDate(state.lastStatusAt)}`;
      $("#eventFreshness").textContent = "odświeżono przed chwilą";
    } catch (error) {
      if (error.name !== "AbortError") {
        setServiceState("offline", "Usługa niedostępna");
        notice("Nie można połączyć się z usługą WorkRouter. Panel będzie próbował ponownie co 2 sekundy.", "warning");
        $("#eventFreshness").textContent = "oczekiwanie na usługę";
      }
    } finally {
      window.clearTimeout(timer); state.polling = false;
    }
  }

  function openConfirm(title, message, action, onCancel = null) {
    const dialog = $("#confirmDialog"); $("#confirmTitle").textContent = title; $("#confirmMessage").textContent = message;
    const accept = $("#confirmAccept"); const cancel = $("#confirmCancel");
    const close = () => { dialog.close(); accept.onclick = null; cancel.onclick = null; };
    cancel.onclick = () => { close(); if (onCancel) onCancel(); }; accept.onclick = async () => { close(); await action(); };
    if (typeof dialog.showModal === "function") dialog.showModal(); else if (window.confirm(message)) action(); else if (onCancel) onCancel();
  }

  async function toggleRouter() {
    if (!state.status) return;
    const start = !state.status.routerRunning;
    const ssid = state.status?.ssid || "WORK";
    openConfirm(start ? "Uruchomić router?" : "Zatrzymać router?", start ? `Hotspot ${ssid} stanie się dostępny dla urządzeń firmowych.` : "Po zatrzymaniu laptop straci Internet i dostęp do udziału Firmowe.", async () => {
      $("#routerToggle").disabled = true;
      try { await api(start ? "/api/router/start" : "/api/router/stop", { method: "POST" }); toast(start ? "Router uruchomiony." : "Router zatrzymany."); await poll(); }
      catch (error) { toast(`Nie udało się ${start ? "uruchomić" : "zatrzymać"} routera.`, "error"); $("#routerToggle").disabled = false; }
    });
  }

  const readBand = () => $("#band").value || $("input[name='bandChoice']:checked")?.value || "5GHz";
  const setBandValue = (band) => { const value = band === "2.4GHz" ? "2.4GHz" : "5GHz"; $("#band").value = value; $("input[name='bandChoice'][value='2.4GHz']").checked = value === "2.4GHz"; $("input[name='bandChoice'][value='5GHz']").checked = value === "5GHz"; };
  const buildWifiPayload = (band) => ({ ssid: $("#ssid").value.trim(), band, maxClients: Number($("#maxClients").value), password: $("#wifiPassword").value || undefined });

  function setBandUiBusy(busy, message) {
    state.bandBusy = busy;
    $("#wifiForm").querySelectorAll("input, select, button").forEach((control) => { control.disabled = busy; });
    $("#bandState").textContent = message;
    if (busy) $("#bandState").className = "field-help is-busy";
    else if (!$("#bandState").classList.contains("is-confirmed") && !$("#bandState").classList.contains("is-error")) $("#bandState").className = "field-help";
  }

  function changeBand(event) {
    const requested = event.target.value;
    const previous = state.status && ["auto", "2.4GHz", "5GHz"].includes(state.status.band) ? state.status.band : "5GHz";
    const wasRunning = Boolean(state.status && state.status.routerRunning);
    if (requested === previous || state.bandBusy) return;
    setBandValue(requested);
    openConfirm("Zmienić pasmo Wi‑Fi?", "WorkRouter wykona kontrolowany restart hotspotu, aby zastosować zmianę. Aktywne połączenia zostaną na chwilę przerwane.", async () => {
      setBandUiBusy(true, "Zapisywanie pasma i kontrolowany restart…");
      try {
        const result = await api("/api/settings", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(buildWifiPayload(requested)) });
        if (result && result.success === false) throw new Error(first(result, ["message", "code"], "backend rejected band change"));
        const activeBand = first(result, ["activeBand", "band"], null);
        const bandConfirmed = result && result.bandConfirmed === true && activeBand === requested;
        if (wasRunning && !bandConfirmed) throw new Error("Backend band confirmation missing");
        const appliedBand = wasRunning ? activeBand : requested;
        const verifiedStatus = normalizeStatus(await api("/api/status") || {});
        state.status = { ...verifiedStatus, band: appliedBand };
        setBandValue(appliedBand);
        renderOverview(state.status);
        $("#bandState").className = "field-help is-confirmed";
        const appliedLabel = requested === "2.4GHz" ? "2,4 GHz" : "5 GHz";
        $("#bandState").textContent = wasRunning ? `Aktywne: ${appliedLabel}. Backend potwierdził zmianę.` : `Ustawione: ${appliedLabel} — zostanie użyte przy uruchomieniu.`;
        $("#wifiForm").dataset.dirty = "false";
        toast(wasRunning ? "Pasmo Wi-Fi zmienione; router uruchomiony ponownie." : "Pasmo Wi-Fi zapisane do następnego uruchomienia.");
      } catch (error) {
        setBandValue(previous);
        $("#wifiForm").dataset.dirty = "false";
        $("#bandState").className = "field-help is-error";
        const conflict = String(error && error.message || "").includes("HTTP 409");
        $("#bandState").textContent = conflict ? "Router jest aktywny lub zmiana jest niedozwolona. Backend nie zastosował pasma." : "Nie udało się potwierdzić zmiany pasma. Przywrócono poprzednią wartość.";
        toast(conflict ? "Backend odrzucił zmianę pasma (409)." : "Zmiana pasma nie została potwierdzona przez backend.", "error");
      } finally { setBandUiBusy(false, $("#bandState").textContent); }
    }, () => { setBandValue(previous); $("#wifiForm").dataset.dirty = "false"; });
  }

  async function saveWifi(event) {
    event.preventDefault();
    const payload = buildWifiPayload(readBand());
    const stateLabel = $("#wifiSaveState"); const wasRunning = Boolean(state.status && state.status.routerRunning); stateLabel.className = "action-state"; stateLabel.textContent = "Zapisywanie…";
    $("#wifiForm").querySelectorAll("input, select, button").forEach((control) => { control.disabled = true; });
    try {
      const result = await api("/api/settings", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      const activeBand = first(result, ["activeBand", "band"], null);
      if (wasRunning && !(result && result.bandConfirmed === true && activeBand === payload.band)) throw new Error("Backend band confirmation missing");
      $("#wifiForm").dataset.dirty = "false"; stateLabel.className = "action-state ok"; stateLabel.textContent = wasRunning ? "Zapisano i potwierdzono" : "Zapisano — użyje przy uruchomieniu"; toast(wasRunning ? "Ustawienia Wi-Fi zapisane i potwierdzone." : "Ustawienia Wi-Fi zapisane do następnego uruchomienia."); await poll();
    } catch (error) { $("#wifiForm").dataset.dirty = "false"; stateLabel.className = "action-state error"; stateLabel.textContent = String(error && error.message || "").includes("HTTP 409") ? "Backend odrzucił zmianę (409)" : "Nie udało się zapisać"; toast("Nie udało się potwierdzić ustawień Wi-Fi.", "error"); try { await poll(); } catch (refreshError) { /* stan błędu pozostaje widoczny */ } }
    finally { $("#wifiForm").querySelectorAll("input, select, button").forEach((control) => { control.disabled = false; }); }
  }

  function rotatePassword() {
    openConfirm("Synchronizować hasło udziału?", "Udział Firmowe używa tego samego hasła co Wi-Fi. Usługa ponownie zastosuje bieżące hasło.", async () => {
      const label = $("#shareActionState"); label.className = "action-state"; label.textContent = "Synchronizowanie…";
      try { await api("/api/share/rotate-password", { method: "POST" }); label.className = "action-state ok"; label.textContent = "Hasło udziału jest takie samo jak Wi-Fi."; toast("Hasło udziału zsynchronizowane z Wi-Fi."); }
      catch (error) { label.className = "action-state error"; label.textContent = "Nie udało się zsynchronizować"; toast("Nie udało się zsynchronizować hasła udziału.", "error"); }
    });
  }

  async function diagnostics() {
    const button = $("#runDiagnostics"); const result = $("#diagnosticsResult"); button.disabled = true; button.textContent = "Sprawdzanie…"; result.className = "diagnostics-result";
    try { const data = await api("/api/diagnostics", { method: "POST" }); const ok = bool(first(data, ["passed", "ok", "success"], true)); result.className = `diagnostics-result ${ok ? "is-ok" : "is-error"}`; $(".diag-mark", result).textContent = ok ? "✓" : "!"; $(".diagnostics-result strong").textContent = first(data, ["summary", "message", "title"], ok ? "Izolacja działa poprawnie" : "Wykryto problem z izolacją"); $(".diagnostics-result span:not(.diag-mark)").textContent = first(data, ["details", "description"], ok ? "Wszystkie sprawdzone bramki odpowiadają zgodnie z polityką." : "Sprawdź szczegóły diagnostyki i zatrzymaj router do czasu wyjaśnienia."); $("#diagnosticsTime").textContent = formatDate(new Date()); toast(ok ? "Diagnostyka zakończona pomyślnie." : "Diagnostyka wykryła problem.", ok ? "ok" : "error"); }
    catch (error) { result.className = "diagnostics-result is-error"; $(".diag-mark", result).textContent = "!"; $(".diagnostics-result strong").textContent = "Diagnostyka niedostępna"; $(".diagnostics-result span:not(.diag-mark)").textContent = "Usługa nie odpowiedziała na żądanie testu."; toast("Nie udało się uruchomić diagnostyki.", "error"); }
    finally { button.disabled = false; button.textContent = "Uruchom test"; }
  }

  function init() {
    $$(".section-nav-link").forEach((link) => link.addEventListener("click", () => { $$(".section-nav-link").forEach((item) => item.classList.toggle("is-active", item === link)); }));
    $("#routerToggle").addEventListener("click", toggleRouter); $("#refreshButton").addEventListener("click", () => { $("#refreshButton").classList.add("is-refreshing"); poll().finally(() => window.setTimeout(() => $("#refreshButton").classList.remove("is-refreshing"), 350)); });
    $("#wifiForm").addEventListener("submit", saveWifi); $("#wifiForm").addEventListener("input", () => { $("#wifiForm").dataset.dirty = "true"; }); $("#wifiForm").addEventListener("change", () => { $("#wifiForm").dataset.dirty = "true"; }); $("#rotatePassword").addEventListener("click", rotatePassword); $("#runDiagnostics").addEventListener("click", diagnostics);
    $$("input[name='bandChoice']").forEach((input) => input.addEventListener("change", changeBand));
    ["trafficTimeFilter", "trafficClientFilter", "trafficRiskFilter"].forEach((id) => document.getElementById(id).addEventListener("change", () => { if (state.status) renderAnalytics(state.status); }));
    ["openPanelAtLogin", "autoStartRouter", "trafficInspectionEnabled", "retentionHours"].forEach((id) => document.getElementById(id).addEventListener("change", saveOperationalSettings));
    $("#clearHistory").addEventListener("click", clearHistory);
    $("#eventList").addEventListener("click", toggleEvent);
    $("#copySharePath").addEventListener("click", async () => { try { await navigator.clipboard.writeText($("#sharePath").textContent); toast("Ścieżka skopiowana."); } catch (error) { toast("Nie udało się skopiować ścieżki.", "error"); } });
    $$(`[data-toggle-password]`).forEach((button) => button.addEventListener("click", () => { const input = document.getElementById(button.dataset.togglePassword); const show = input.type !== "text"; input.type = show ? "text" : "password"; button.textContent = show ? "Ukryj" : "Pokaż"; button.setAttribute("aria-pressed", String(show)); button.setAttribute("aria-label", show ? "Ukryj hasło sieci" : "Pokaż hasło sieci"); }));
    poll(); window.setInterval(poll, POLL_MS);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
})();
