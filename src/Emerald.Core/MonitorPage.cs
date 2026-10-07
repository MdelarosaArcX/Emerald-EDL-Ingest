namespace Emerald.Core;

/// <summary>
/// The page a remote machine gets.
///
/// One file, no build step, nothing to install: a browser on any PC, tablet or phone on the
/// network opens it and sees what the master's own monitoring page shows. It polls the same
/// read-only JSON the master serves — the strip twice a second, the log by cursor so a page
/// left open all night catches up in one request rather than re-reading everything.
///
/// Held as a raw string literal rather than an embedded resource so that it is visible in the
/// source next to the server that serves it, and so a colour or a column can be changed
/// without touching the project file.
/// </summary>
public static class MonitorPage
{
    public const string Html = """
        <!doctype html>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Emerald - Monitor</title>
        <style>
        :root{--bg:#14171C;--panel:#1C2027;--deep:#0B0E12;--line:#2A3039;--text:#E6EAF0;
        --muted:#8A93A3;--accent:#3B82F6;--ok:#22C55E;--warn:#F59E0B;--bad:#EF4444;--tc:#7DD3FC}
        *{box-sizing:border-box}
        body{margin:0;background:var(--bg);color:var(--text);font:13px/1.45 "Segoe UI",system-ui,sans-serif}
        .wrap{padding:14px;max-width:1900px;margin:0 auto}
        header{display:flex;align-items:center;gap:14px;margin-bottom:12px;flex-wrap:wrap}
        h1{font-size:15px;margin:0;letter-spacing:.5px}
        .tc{font:700 20px/1 Consolas,monospace;color:var(--tc)}
        .dot{width:8px;height:8px;border-radius:50%;background:var(--bad);display:inline-block}
        .dot.on{background:var(--ok)}
        .hint{color:var(--muted);font-size:11.5px}
        .strip{display:grid;grid-template-columns:repeat(5,1fr);gap:10px;margin-bottom:12px}
        .card{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:10px 12px;min-width:0}
        .lbl{font-size:9.5px;letter-spacing:1.1px;color:var(--muted);font-weight:700;margin-bottom:5px}
        .head{font-size:13.5px;font-weight:700;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .det{font-size:11px;color:var(--muted);margin-top:2px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .frames{font:11px Consolas,monospace;color:var(--tc);margin-top:5px}
        .ok{color:var(--ok)}.warn{color:var(--warn)}.bad{color:var(--bad)}.dim{color:var(--muted)}
        .bar{display:flex;gap:8px;align-items:center;background:var(--panel);border:1px solid var(--line);
        border-radius:6px;padding:8px 10px;margin-bottom:10px;flex-wrap:wrap}
        input,select,button{background:var(--deep);color:var(--text);border:1px solid var(--line);
        border-radius:4px;padding:5px 8px;font:inherit;font-size:12px}
        button{cursor:pointer}button:hover{border-color:var(--accent)}
        button.sel{border-color:var(--accent);color:var(--tc)}
        .grid{background:var(--panel);border:1px solid var(--line);border-radius:6px;overflow:hidden}
        table{width:100%;border-collapse:collapse;font:11.5px/1.5 Consolas,monospace}
        thead th{position:sticky;top:0;background:#171B22;color:var(--muted);font-size:9.5px;letter-spacing:.8px;
        text-align:left;padding:7px 8px;border-bottom:1px solid var(--line);font-weight:700}
        td{padding:3px 8px;border-bottom:1px solid #20252D;vertical-align:top;white-space:nowrap;
        overflow:hidden;text-overflow:ellipsis}
        td.msg{white-space:normal}
        tbody tr:hover{background:#20252E}
        .scroll{max-height:calc(100vh - 340px);overflow:auto}
        .pill{font-size:9.5px;font-weight:700;letter-spacing:.5px}
        .grp td{background:#141922;color:var(--tc);font-weight:700}
        .empty{padding:20px;color:var(--muted);text-align:center}
        @media(max-width:1100px){.strip{grid-template-columns:repeat(2,1fr)}}
        </style>

        <div class="wrap">
          <header>
            <h1>EMERALD MONITOR</h1>
            <span><span class="dot" id="live"></span> <span class="hint" id="conn">connecting...</span></span>
            <span class="tc" id="tc">--:--:--:--</span>
            <span class="hint" id="host"></span>
            <span style="margin-left:auto"></span>
            <button id="tabLog" class="sel">ACTIVITY</button>
            <button id="tabTx">TRANSMITTED</button>
          </header>

          <div class="strip" id="strip"></div>

          <div id="logView">
            <div class="bar">
              <select id="src"><option value="">All sources</option></select>
              <select id="lvl">
                <option value="0">Info and up</option><option value="1">Ok and up</option>
                <option value="2">Warn and up</option><option value="3">Errors only</option>
              </select>
              <input id="q" placeholder="message, file or id" size="26">
              <input id="idq" placeholder="id" size="10">
              <button id="clear">All</button>
              <button id="follow" class="sel">FOLLOW</button>
              <span class="hint" id="count"></span>
            </div>
            <div class="grid"><div class="scroll" id="logScroll">
              <table>
                <thead><tr><th style="width:92px">Time</th><th style="width:100px">Timecode</th>
                <th style="width:84px">Source</th><th style="width:78px">Id</th>
                <th style="width:170px">File</th><th>Message</th></tr></thead>
                <tbody id="rows"></tbody>
              </table>
            </div></div>
          </div>

          <div id="txView" hidden>
            <div class="bar"><span class="hint" id="txCount"></span>
              <span class="hint">Picture and each language are separate rows - they go to air on their own.</span>
            </div>
            <div class="grid"><div class="scroll">
              <table>
                <thead><tr><th style="width:84px">Kind</th><th style="width:130px">Track</th>
                <th style="width:52px">Ch</th><th style="width:330px">File</th>
                <th style="width:74px">State</th><th style="width:86px">To air</th>
                <th style="width:86px">Last</th></tr></thead>
                <tbody id="txRows"></tbody>
              </table>
            </div></div>
          </div>
        </div>

        <script>
        const LEVELS = ["Info","Ok","Warn","Error"];
        const CLS = {Info:"", Ok:"ok", Warn:"warn", Error:"bad"};
        let since = 0, lines = [], follow = true, tab = "log", sources = new Set(), misses = 0;

        const $ = id => document.getElementById(id);
        const esc = s => (s ?? "").replace(/[&<>]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;"}[c]));

        $("host").textContent = location.host;

        function show(which){
          tab = which;
          $("logView").hidden = which !== "log";
          $("txView").hidden = which !== "tx";
          $("tabLog").className = which === "log" ? "sel" : "";
          $("tabTx").className = which === "tx" ? "sel" : "";
          if (which === "tx") loadTransmitted();
        }
        $("tabLog").onclick = () => show("log");
        $("tabTx").onclick = () => show("tx");

        ["src","lvl","q","idq"].forEach(id => $(id).oninput = $(id).onchange = draw);
        $("clear").onclick = () => {
          $("src").value = ""; $("lvl").value = "0"; $("q").value = ""; $("idq").value = ""; draw();
        };
        $("follow").onclick = () => {
          follow = !follow; $("follow").className = follow ? "sel" : ""; if (follow) tail();
        };
        $("logScroll").onscroll = e => {
          const el = e.target, atEnd = el.scrollTop + el.clientHeight >= el.scrollHeight - 30;
          if (!atEnd && follow) { follow = false; $("follow").className = ""; }
        };
        const tail = () => { const el = $("logScroll"); el.scrollTop = el.scrollHeight; };

        function matches(l){
          if (LEVELS.indexOf(l.level) < +$("lvl").value) return false;
          if ($("src").value && l.source !== $("src").value) return false;
          const id = $("idq").value.trim().toLowerCase();
          if (id && !(l.id ?? "").toLowerCase().includes(id)) return false;
          const q = $("q").value.trim().toLowerCase();
          if (!q) return true;
          return (l.message + " " + (l.file ?? "") + " " + (l.id ?? "") + " " + (l.event ?? ""))
                 .toLowerCase().includes(q);
        }

        function draw(){
          const shown = lines.filter(matches);
          $("rows").innerHTML = shown.map(l => `<tr>
            <td class="dim">${esc(l.time)}</td>
            <td style="color:var(--tc)">${esc(l.timecode ?? "--:--:--:--")}</td>
            <td class="dim">${esc(l.source)}</td>
            <td class="dim" title="${esc(l.id)}">${esc(l.id ?? "")}</td>
            <td class="dim" title="${esc(l.path ?? "")}">${esc(l.file ?? "")}</td>
            <td class="msg ${CLS[l.level]}">${esc(l.message)}</td></tr>`).join("");
          $("count").textContent = `${shown.length} of ${lines.length} line(s)`;
          if (follow) tail();
        }

        function stage(label, s, extra){
          const cls = s.idle ? "dim" : CLS[s.level] || "";
          return `<div class="card"><div class="lbl">${label}</div>
            <div class="head ${cls}">${esc(s.headline || "idle")}</div>
            <div class="det" title="${esc(s.detail)}">${esc(s.detail)}</div>
            ${extra ?? ""}</div>`;
        }

        function drawStrip(s){
          $("tc").textContent = s.timecode ?? "--:--:--:--";
          const f = s.frames, d = s.delay;

          let capFrames = "";
          if (s.recording || f.recorded > 0){
            const lost = f.notEncoded > 0 || f.notDelayed > 0;
            capFrames = `<div class="frames ${lost ? "bad" : ""}">${f.recorded.toLocaleString()} frames`
              + (f.notEncoded > 0 ? ` &middot; ${f.notEncoded.toLocaleString()} NOT ENCODED` : " &middot; 0 dropped")
              + (f.notDelayed > 0 ? ` &middot; ${f.notDelayed.toLocaleString()} NOT DELAYED` : "") + `</div>`;
          }

          let airFrames = "";
          if (f.aired > 0){
            const behind = Math.max(0, f.recorded - f.aired - d.targetFrames);
            const bad = f.skipped > 0 || behind > 0;
            airFrames = `<div class="frames ${bad ? "bad" : f.repeated > 0 ? "warn" : ""}">`
              + `${f.aired.toLocaleString()} out of ${f.recorded.toLocaleString()} &middot; delay ${d.targetFrames.toLocaleString()}`
              + (behind > 0 ? ` &middot; ${behind.toLocaleString()} BEHIND` : "")
              + (f.skipped > 0 ? ` &middot; ${f.skipped.toLocaleString()} SKIPPED` : "")
              + (f.repeated > 0 ? ` &middot; ${f.repeated.toLocaleString()} repeated` : "")
              + (!bad && f.repeated === 0 ? " &middot; no drops" : "") + `</div>`;
          }

          const phase = d.phase === "Off"
            ? {headline:"idle", detail:"", idle:true, level:"Info"}
            : {headline: d.phase === "CountingDown" ? `FILLING ${Math.round(d.fill*100)}%` : d.phase.toUpperCase(),
               detail: d.detail, idle:false,
               level: d.phase === "OnAir" ? "Ok" : d.phase === "Failed" ? "Error" : "Warn"};

          $("strip").innerHTML =
            stage("CAPTURE", s.capture, capFrames) +
            stage("TIDAL LOCK", phase, airFrames) +
            stage("EDL QUEUE", s.edl) +
            stage("ON AIR", s.onAir) +
            stage("INGEST", s.ingest);
        }

        async function loadTransmitted(){
          try{
            const d = await (await fetch("api/transmitted")).json();
            let html = "", files = 0, aired = 0;
            for (const e of d.edls){
              html += `<tr class="grp"><td colspan="7">${esc(e.headline || ("EDL " + e.id))}</td></tr>`;
              for (const f of e.files){
                files++; if (f.state === "AIRED") aired++;
                html += `<tr><td class="pill ${f.kind === "VIDEO" ? "" : "ok"}">${esc(f.kind)}</td>
                  <td class="dim">${esc(f.label)}</td><td class="dim">${esc(f.channels)}</td>
                  <td title="${esc(f.path)}">${esc(f.name)}</td>
                  <td class="pill ${f.state === "AIRED" ? "ok" : "dim"}">${esc(f.state)}</td>
                  <td class="dim">${esc(f.timesText)}</td>
                  <td style="color:var(--tc)">${esc(f.lastAired)}</td></tr>`;
              }
            }
            $("txRows").innerHTML = html
              || `<tr><td colspan="7" class="empty">Nothing has gone to air yet.</td></tr>`;
            $("txCount").textContent =
              `${aired} of ${files} file(s) to air, ${d.edls.length} message(s)`;
          } catch(e){ /* the poll below is what reports the connection */ }
        }

        async function poll(){
          try{
            const s = await (await fetch("api/state")).json();
            drawStrip(s);

            const l = await (await fetch("api/log?since=" + since + "&limit=1000")).json();
            if (l.lines.length){
              for (const line of l.lines){
                lines.push(line);
                if (!sources.has(line.source)){
                  sources.add(line.source);
                  const o = document.createElement("option");
                  o.value = o.textContent = line.source;
                  $("src").appendChild(o);
                }
              }
              since = l.lastSeq;
              if (lines.length > 5000) lines = lines.slice(-5000);
              if (tab === "log") draw();
            }

            misses = 0;
            $("live").className = "dot on";
            $("conn").textContent = "connected"
              + (s.dropped > 0 ? ` - ${s.dropped} line(s) dropped on the master` : "");
          } catch(e){
            if (++misses > 1){
              $("live").className = "dot";
              $("conn").textContent = "cannot reach the master - retrying";
            }
          }
        }

        poll();
        setInterval(poll, 500);
        setInterval(() => { if (tab === "tx") loadTransmitted(); }, 3000);
        </script>
        """;
}
