import React, { useState, useEffect, useRef } from 'react';
import * as signalR from '@microsoft/signalr';
import { 
  Play, Square, Settings, Activity, FileCheck, ShieldAlert, 
  FlaskConical, Calendar, ListFilter, LayoutDashboard, Terminal,
  ChevronDown, ChevronRight, Search, Clock, AlertTriangle, Info, CheckCircle2,
  Filter, SortAsc, SortDesc, Database
} from 'lucide-react';

const API_BASE = "http://localhost:5100";

function App() {
  const [activeTab, setActiveTab] = useState('dashboard');
  const [isConfigured, setIsConfigured] = useState(localStorage.getItem('dedupe_configured') === 'true');
  const [status, setStatus] = useState({
    isRunning: false,
    currentPhase: "Idle",
    filesDiscovered: 0,
    totalBytesHashed: 0,
    successCount: 0,
    failCount: 0,
    elapsedMinutes: 0,
    config: { source: "", archive: "", suspect: "", isScheduled: false, scheduleTime: "02:00" }
  });

  const [config, setConfig] = useState({
    source: "",
    archive: "",
    suspect: "",
    isScheduled: false,
    scheduleTime: "02:00",
    telemetryUrl: "",
    telemetryToken: ""
  });

  const [logs, setLogs] = useState([]);
  const [labLogs, setLabLogs] = useState([]);
  const [splunkLogs, setSplunkLogs] = useState([]);
  const [expandedLogs, setExpandedLogs] = useState(new Set());
  const [logFilter, setLogFilter] = useState("");
  const [severityFilter, setSeverityFilter] = useState("ALL");
  const [sortOrder, setSortOrder] = useState('desc');

  const logEndRef = useRef(null);
  const labLogEndRef = useRef(null);

  useEffect(() => {
    if (!isConfigured) return;

    fetchStatus();
    const interval = setInterval(fetchStatus, 3000);

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE}/logHub`)
      .withAutomaticReconnect()
      .build();

    connection.on("ReceiveLog", (message, severity) => {
      const logEntry = { message, severity, time: new Date().toLocaleTimeString() };
      setLogs(prev => [...prev.slice(-199), logEntry]);
      if (message.includes("[PS]") || message.includes("[TEST LAB]")) {
        setLabLogs(prev => [...prev.slice(-1000), logEntry]);
      }
    });

    connection.start().catch(err => console.error("SignalR Connection Error: ", err));

    return () => {
      clearInterval(interval);
      connection.stop();
    };
  }, [isConfigured]);

  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [logs]);

  useEffect(() => {
    labLogEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [labLogs]);

  useEffect(() => {
    if (activeTab === 'splunk') fetchSplunkLogs();
  }, [activeTab]);

  const fetchStatus = async () => {
    try {
      const res = await fetch(`${API_BASE}/api/dedupe/status`);
      const data = await res.json();
      setStatus(data);
      if (!data.isRunning && data.config.source && !config.source) {
        setConfig(prev => ({ ...prev, ...data.config }));
      }
    } catch (e) {}
  };

  const fetchSplunkLogs = async () => {
    try {
      const res = await fetch(`${API_BASE}/api/dedupe/logs`);
      const data = await res.json();
      setSplunkLogs(Array.isArray(data) ? data : []);
    } catch (e) {
      setSplunkLogs([{ message: "Error fetching Splunk logs or Splunk REST API unreachable.", severity: "Red" }]);
    }
  };

  const toggleLogExpansion = (index) => {
    const next = new Set(expandedLogs);
    if (next.has(index)) next.delete(index);
    else next.add(index);
    setExpandedLogs(next);
  };

  const getFilteredSplunkLogs = () => {
    let list = [...splunkLogs];
    if (severityFilter !== "ALL") {
      list = list.filter(l => (l.severity || "").toUpperCase() === severityFilter);
    }
    if (logFilter) {
      const lower = logFilter.toLowerCase();
      list = list.filter(l => 
        (l.message || "").toLowerCase().includes(lower) || 
        JSON.stringify(l).toLowerCase().includes(lower)
      );
    }
    return list.sort((a, b) => {
      const timeA = new Date(a.time).getTime();
      const timeB = new Date(b.time).getTime();
      return sortOrder === 'desc' ? timeB - timeA : timeA - timeB;
    });
  };

  const updateConfig = async () => {
    await fetch(`${API_BASE}/api/dedupe/config`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(config)
    });
    alert("Configuration & Schedule Saved!");
  };

  const startRun = () => fetch(`${API_BASE}/api/dedupe/start`, { method: 'POST' });
  const stopRun = () => fetch(`${API_BASE}/api/dedupe/stop`, { method: 'POST' });
  const runTestScript = () => fetch(`${API_BASE}/api/dedupe/testscript`, { method: 'POST' });
  const runSafetyTest = () => fetch(`${API_BASE}/api/dedupe/verify`, { method: 'POST' });

  if (!isConfigured) {
    return <SetupWizard onComplete={() => setIsConfigured(true)} />;
  }

  const filteredSplunk = getFilteredSplunkLogs();

  return (
    <div className="min-h-screen bg-slate-950 text-slate-200 font-sans flex flex-col">
      <nav className="bg-slate-900 border-b border-slate-800 p-4 flex justify-between items-center shadow-2xl">
        <div className="flex items-center gap-6">
          <h1 className="text-2xl font-black text-cyan-500 tracking-tighter flex items-center gap-2 italic">
            <Activity className="w-8 h-8 text-cyan-400" /> NAS-DEDUPER <span className="text-xs font-bold bg-cyan-900 text-cyan-200 px-2 py-0.5 rounded italic not-italic tracking-normal ml-2">ENT</span>
          </h1>
          <div className="flex gap-1 bg-slate-950 p-1 rounded-lg border border-slate-800">
            <TabButton active={activeTab === 'dashboard'} onClick={() => setActiveTab('dashboard')} icon={<LayoutDashboard size={18}/>} label="Dashboard" />
            <TabButton active={activeTab === 'config'} onClick={() => setActiveTab('config')} icon={<Settings size={18}/>} label="Config & Schedule" />
            <TabButton active={activeTab === 'lab'} onClick={() => setActiveTab('lab')} icon={<FlaskConical size={18}/>} label="Test Lab" />
            <TabButton active={activeTab === 'splunk'} onClick={() => setActiveTab('splunk')} icon={<ListFilter size={18}/>} label="Splunk Logs" />
          </div>
        </div>
        
        <div className="flex gap-3">
          <button onClick={() => { localStorage.removeItem('dedupe_configured'); window.location.reload(); }} className="text-slate-500 hover:text-white px-3 py-2 text-xs font-bold uppercase tracking-widest">Reset Connection</button>
          <button onClick={runSafetyTest} disabled={status.isRunning} className="bg-amber-600/20 hover:bg-amber-600 text-amber-400 hover:text-white px-4 py-2 rounded-lg font-bold flex items-center gap-2 border border-amber-600/30 transition-all disabled:opacity-30">
            <ShieldAlert size={16} /> Safety Test
          </button>
          {!status.isRunning ? (
            <button onClick={startRun} className="bg-green-600 hover:bg-green-500 text-white px-6 py-2 rounded-lg font-bold flex items-center gap-2 shadow-lg shadow-green-900/20 transition-all">
              <Play size={16} fill="currentColor" /> Start Engine
            </button>
          ) : (
            <button onClick={stopRun} className="bg-red-600 hover:bg-red-500 text-white px-6 py-2 rounded-lg font-bold flex items-center gap-2 shadow-lg shadow-red-900/20 transition-all animate-pulse">
              <Square size={16} fill="currentColor" /> Stop Engine
            </button>
          )}
        </div>
      </nav>

      <main className="flex-1 p-6 overflow-hidden">
        {activeTab === 'dashboard' && (
          <div className="grid grid-cols-1 lg:grid-cols-4 gap-6 h-full">
            <div className="lg:col-span-1 space-y-6">
               <div className="bg-slate-900 rounded-2xl border border-slate-800 p-5 shadow-xl">
                  <h2 className="text-xs font-bold text-slate-500 uppercase tracking-widest mb-4">Real-Time Metrics</h2>
                  <div className="space-y-4">
                     <MetricCard label="Current Phase" value={status.currentPhase} color="text-cyan-400" />
                     <div className="grid grid-cols-2 gap-4">
                        <MetricCard label="Discovered" value={status.filesDiscovered.toLocaleString()} />
                        <MetricCard label="Hashed" value={`${(status.totalBytesHashed / (1024**3)).toFixed(2)} GB`} />
                     </div>
                     <div className="grid grid-cols-2 gap-4">
                        <MetricCard label="Success" value={status.successCount.toLocaleString()} color="text-green-400" />
                        <MetricCard label="Failures" value={status.failCount.toLocaleString()} color="text-red-400" />
                     </div>
                  </div>
               </div>

               <div className="bg-slate-900 rounded-2xl border border-slate-800 p-5 shadow-xl">
                  <h2 className="text-xs font-bold text-slate-500 uppercase tracking-widest mb-4">System Status</h2>
                  <div className="space-y-4">
                    <div className="flex items-center gap-3 p-3 bg-slate-950 rounded-xl border border-slate-800">
                      <div className={`w-3 h-3 rounded-full ${status.isRunning ? 'bg-green-500 animate-ping' : 'bg-slate-700'}`}></div>
                      <span className="font-bold text-sm">{status.isRunning ? 'Engine Operational' : 'Engine Idle'}</span>
                    </div>

                    {!status.isRunning && status.config.source && !status.config.source.startsWith('/') && !status.config.source.startsWith('\\\\') && (
                      <div className="p-4 bg-amber-900/20 border border-amber-900/50 rounded-xl text-xs text-amber-200">
                        <p className="font-bold mb-1 flex items-center gap-2"><ShieldAlert size={14}/> Container Path Warning</p>
                        <p>The current path looks like a Windows local path. Since this is running in a container, use <b>Remote NAS (SMB)</b> mode to mount it, or map the host folder to a container volume.</p>
                      </div>
                    )}
                  </div>
               </div>
            </div>

            <div className="lg:col-span-3 flex flex-col bg-slate-900 rounded-2xl border border-slate-800 overflow-hidden shadow-2xl">
               <div className="bg-slate-800/50 p-4 border-b border-slate-800 flex justify-between items-center">
                  <h2 className="text-sm font-bold flex items-center gap-2"><Terminal size={16} className="text-cyan-400"/> Live Execution Stream</h2>
               </div>
               <div className="flex-1 overflow-y-auto p-4 font-mono text-xs space-y-1 bg-black/40">
                  {logs.map((log, i) => (
                    <div key={i} className="flex gap-4 border-b border-white/5 py-1 group">
                      <span className="text-slate-600 shrink-0">{log.time}</span>
                      <span className={
                        log.severity.includes("Red") ? "text-red-400" :
                        log.severity.includes("Yellow") ? "text-yellow-400" :
                        log.severity.includes("Cyan") ? "text-cyan-400" :
                        log.severity.includes("Green") ? "text-green-400" :
                        "text-slate-300"
                      }>{log.message}</span>
                    </div>
                  ))}
                  <div ref={logEndRef} />
               </div>
            </div>
          </div>
        )}

        {activeTab === 'config' && (
          <div className="max-w-4xl mx-auto grid grid-cols-1 md:grid-cols-2 gap-8">
            <section className="bg-slate-900 p-8 rounded-3xl border border-slate-800 shadow-2xl">
              <h2 className="text-xl font-bold mb-6 flex items-center gap-3 text-cyan-400"><Settings /> Path Configuration</h2>
              <div className="space-y-6">
                <ConfigInput label="Source Root" value={config.source} onChange={v => setConfig({...config, source: v})} />
                <ConfigInput label="Archive Target" value={config.archive} onChange={v => setConfig({...config, archive: v})} />
                <ConfigInput label="Suspect Target" value={config.suspect} onChange={v => setConfig({...config, suspect: v})} />
              </div>
            </section>

            <section className="bg-slate-900 p-8 rounded-3xl border border-slate-800 shadow-2xl flex flex-col">
              <h2 className="text-xl font-bold mb-6 flex items-center gap-3 text-cyan-400"><Calendar /> Automated Scheduling</h2>
              <div className="space-y-6 flex-1">
                <div className="flex items-center justify-between p-4 bg-slate-950 rounded-2xl border border-slate-800">
                  <div>
                    <p className="font-bold">Enable Daily Run</p>
                    <p className="text-xs text-slate-500">Triggers deduplication at specified time</p>
                  </div>
                  <input type="checkbox" checked={config.isScheduled} onChange={e => setConfig({...config, isScheduled: e.target.checked})} className="w-6 h-6 accent-cyan-500" />
                </div>
                <input type="time" value={config.scheduleTime} onChange={e => setConfig({...config, scheduleTime: e.target.value})} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-2xl text-xl font-mono focus:border-cyan-500 outline-none" />
              </div>
              <button onClick={updateConfig} className="mt-8 w-full bg-cyan-600 hover:bg-cyan-500 text-white py-4 rounded-2xl font-black uppercase tracking-widest shadow-lg transition-all">
                Save & Apply Configuration
              </button>
            </section>
          </div>
        )}

        {activeTab === 'lab' && (
          <div className="max-w-5xl mx-auto h-full flex flex-col gap-6">
            <div className="bg-slate-900 p-8 rounded-3xl border border-slate-800 shadow-2xl flex justify-between items-center">
              <div>
                <h2 className="text-2xl font-bold text-cyan-400 mb-2 flex items-center gap-3"><FlaskConical /> Simulation Lab</h2>
                <p className="text-slate-400">Generate complex recursive mock data to verify engine logic safely.</p>
              </div>
              <button onClick={runTestScript} disabled={status.isRunning} className="bg-indigo-600 hover:bg-indigo-500 px-8 py-4 rounded-2xl font-black uppercase tracking-widest flex items-center gap-3 transition-all disabled:opacity-30">
                <Terminal size={20} /> Run Simulation
              </button>
            </div>
            
            <div className="flex-1 bg-black rounded-3xl border border-slate-800 overflow-hidden flex flex-col shadow-inner">
               <div className="p-4 bg-slate-900 border-b border-slate-800 text-xs font-bold text-slate-500 tracking-widest">CONSOLE OUTPUT</div>
               <div className="flex-1 p-6 font-mono text-sm overflow-y-auto text-indigo-300 space-y-1">
                  {labLogs.map((log, i) => (
                    <div key={i}>{log.message.includes("[PS]") ? log.message.substring(log.message.indexOf("[PS]") + 5) : log.message}</div>
                  ))}
                  {labLogs.length === 0 && <div className="text-slate-700 italic">No simulation logs in buffer.</div>}
                  <div ref={labLogEndRef} />
               </div>
            </div>
          </div>
        )}

        {activeTab === 'splunk' && (
          <div className="max-w-7xl mx-auto h-full flex flex-col gap-4">
             {/* Enhanced Splunk Toolbar */}
             <div className="bg-slate-900 p-4 rounded-2xl border border-slate-800 shadow-xl flex flex-wrap gap-4 items-center justify-between">
                <div className="flex gap-4 items-center flex-1">
                  <div className="relative flex-1 max-w-md">
                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-500" size={16} />
                    <input 
                      value={logFilter} 
                      onChange={e => setLogFilter(e.target.value)} 
                      placeholder="Search enterprise logs..." 
                      className="w-full bg-slate-950 border border-slate-800 pl-10 pr-4 py-2 rounded-xl text-sm focus:border-cyan-500 outline-none transition-all"
                    />
                  </div>
                  <select 
                    value={severityFilter} 
                    onChange={e => setSeverityFilter(e.target.value)}
                    className="bg-slate-950 border border-slate-800 px-4 py-2 rounded-xl text-sm focus:border-cyan-500 outline-none"
                  >
                    <option value="ALL">All Severities</option>
                    <option value="CYAN">Phase Changes</option>
                    <option value="GREEN">Successes</option>
                    <option value="YELLOW">Warnings</option>
                    <option value="RED">Errors</option>
                  </select>
                </div>

                <div className="flex gap-2 items-center">
                  <button 
                    onClick={() => setSortOrder(sortOrder === 'desc' ? 'asc' : 'desc')}
                    className="p-2 bg-slate-800 hover:bg-slate-700 rounded-xl transition-all flex items-center gap-2 text-xs font-bold"
                  >
                    {sortOrder === 'desc' ? <SortDesc size={16}/> : <SortAsc size={16}/>} 
                    {sortOrder === 'desc' ? 'Newest First' : 'Oldest First'}
                  </button>
                  <button onClick={fetchSplunkLogs} className="bg-cyan-600 hover:bg-cyan-500 px-4 py-2 rounded-xl text-sm font-black transition-all">REFRESH</button>
                </div>
             </div>
             
             {/* Advanced Log Table */}
             <div className="flex-1 bg-slate-900 rounded-3xl border border-slate-800 shadow-2xl overflow-hidden flex flex-col">
                <div className="overflow-y-auto flex-1">
                  <table className="w-full text-left border-collapse">
                     <thead className="bg-slate-800/50 text-[10px] font-bold text-slate-500 uppercase tracking-widest sticky top-0 backdrop-blur-md z-10">
                        <tr>
                          <th className="p-4 border-b border-slate-700 w-10"></th>
                          <th className="p-4 border-b border-slate-700 w-48">Timestamp</th>
                          <th className="p-4 border-b border-slate-700">Event / Message</th>
                          <th className="p-4 border-b border-slate-700 w-32 text-center">Status</th>
                        </tr>
                     </thead>
                     <tbody className="font-mono text-sm">
                        {filteredSplunk.map((log, i) => (
                          <React.Fragment key={i}>
                            <tr 
                              onClick={() => toggleLogExpansion(i)}
                              className={`hover:bg-slate-800/40 cursor-pointer transition-colors border-b border-slate-800/50 ${expandedLogs.has(i) ? 'bg-slate-800/20' : ''}`}
                            >
                              <td className="p-4 text-slate-600">
                                {expandedLogs.has(i) ? <ChevronDown size={14}/> : <ChevronRight size={14}/>}
                              </td>
                              <td className="p-4 text-slate-500 whitespace-nowrap text-xs flex items-center gap-2">
                                <Clock size={12} className="opacity-50"/> {new Date(log.time).toLocaleString()}
                              </td>
                              <td className="p-4">
                                <span className={
                                  log.severity?.includes("Red") ? "text-red-400" :
                                  log.severity?.includes("Yellow") ? "text-yellow-400" :
                                  log.severity?.includes("Cyan") ? "text-cyan-400" :
                                  log.severity?.includes("Green") ? "text-green-400" :
                                  "text-slate-300"
                                }>{log.message}</span>
                              </td>
                              <td className="p-4 text-center">
                                {log.severity?.includes("Red") && <div className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-red-900/30 text-red-400 text-[10px] font-bold"><AlertTriangle size={10}/> ERROR</div>}
                                {log.severity?.includes("Cyan") && <div className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-cyan-900/30 text-cyan-400 text-[10px] font-bold"><Database size={10}/> PHASE</div>}
                                {log.severity?.includes("Green") && <div className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-green-900/30 text-green-400 text-[10px] font-bold"><CheckCircle2 size={10}/> SUCCESS</div>}
                                {!log.severity && <div className="inline-flex items-center gap-1 px-2 py-0.5 rounded bg-slate-800 text-slate-400 text-[10px] font-bold"><Info size={10}/> INFO</div>}
                              </td>
                            </tr>
                            {expandedLogs.has(i) && (
                              <tr className="bg-black/40 border-b border-slate-800/50">
                                <td colSpan="4" className="p-6">
                                   <div className="bg-slate-950 p-4 rounded-xl border border-slate-800 shadow-inner">
                                      <h4 className="text-[10px] font-black text-slate-600 uppercase tracking-widest mb-3">Extended Event Metadata</h4>
                                      <pre className="text-xs text-cyan-700/80 overflow-x-auto whitespace-pre-wrap leading-relaxed">
                                        {JSON.stringify(log, null, 2)}
                                      </pre>
                                   </div>
                                </td>
                              </tr>
                            )}
                          </React.Fragment>
                        ))}
                        {filteredSplunk.length === 0 && (
                          <tr>
                            <td colSpan="4" className="p-20 text-center flex flex-col items-center gap-4">
                               <Database className="w-12 h-12 text-slate-800" />
                               <div className="text-slate-600 italic">No enterprise logs found matching current filters.</div>
                            </td>
                          </tr>
                        )}
                     </tbody>
                  </table>
                </div>
                <div className="p-3 bg-slate-800/30 border-t border-slate-800 text-[10px] font-bold text-slate-500 uppercase tracking-widest flex justify-between">
                   <span>Viewing {filteredSplunk.length} of {splunkLogs.length} indexed events</span>
                   <span>Enterprise Splunk Proxy Connected</span>
                </div>
             </div>
          </div>
        )}
      </main>

      <footer className="p-4 bg-slate-900/50 border-t border-slate-800 flex justify-between items-center text-[10px] font-bold text-slate-500 tracking-widest uppercase">
         <div>NAS DEDUPLICATOR ENTERPRISE v1.3.0</div>
      </footer>
    </div>
  );
}

function SetupWizard({ onComplete }) {
  const [type, setType] = useState(0); // 0 = Local, 1 = SMB
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [form, setForm] = useState({
    networkPath: "", username: "", password: "",
    sourcePath: "C:\\Temp\\DedupeManualTest\\Source",
    archivePath: "C:\\Temp\\DedupeManualTest\\Archive",
    suspectPath: "C:\\Temp\\DedupeManualTest\\Suspect"
  });

  const handleConnect = async () => {
    setLoading(true);
    setError("");
    try {
      const res = await fetch(`${API_BASE}/api/dedupe/connect`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          type: type,
          networkPath: form.networkPath,
          username: form.username,
          password: form.password,
          sourcePath: form.sourcePath,
          archivePath: form.archivePath,
          suspectPath: form.suspectPath
        })
      });

      if (res.ok) {
        localStorage.setItem('dedupe_configured', 'true');
        onComplete();
      } else {
        const msg = await res.text();
        setError(msg || "Failed to connect to target.");
      }
    } catch (e) {
      setError("Backend server unreachable. Ensure backend is running.");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="min-h-screen bg-slate-950 flex items-center justify-center p-6 font-sans">
      <div className="max-w-2xl w-full bg-slate-900 rounded-[3rem] border border-slate-800 shadow-2xl overflow-hidden flex flex-col">
        <div className="p-12 pb-6 text-center">
          <div className="inline-flex p-4 bg-cyan-500/10 rounded-3xl mb-6">
            <Activity className="w-12 h-12 text-cyan-400" />
          </div>
          <h1 className="text-4xl font-black text-white mb-2 tracking-tight">Enterprise Onboarding</h1>
          <p className="text-slate-500">Initialize your NAS Deduplication connection</p>
        </div>

        <div className="px-12 pb-12 flex-1 space-y-8">
          <div className="flex gap-4 p-1 bg-slate-950 rounded-2xl border border-slate-800">
            <button onClick={() => setType(0)} className={`flex-1 py-3 rounded-xl font-bold transition-all ${type === 0 ? 'bg-slate-800 text-white shadow-xl' : 'text-slate-500'}`}>Local Machine</button>
            <button onClick={() => setType(1)} className={`flex-1 py-3 rounded-xl font-bold transition-all ${type === 1 ? 'bg-slate-800 text-white shadow-xl' : 'text-slate-500'}`}>Remote NAS (SMB)</button>
          </div>

          <div className="grid grid-cols-1 gap-6">
            {type === 1 && (
              <>
                <ConfigInput label="Network Path (UNC)" value={form.networkPath} onChange={v => setForm({...form, networkPath: v})} placeholder="\\192.168.1.100\Data" />
                <div className="grid grid-cols-2 gap-4">
                  <ConfigInput label="Username" value={form.username} onChange={v => setForm({...form, username: v})} placeholder="admin" />
                  <div>
                    <label className="text-xs font-bold text-slate-500 uppercase tracking-widest block mb-2">Password</label>
                    <input type="password" value={form.password} onChange={e => setForm({...form, password: e.target.value})} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-2xl text-sm font-mono focus:border-cyan-500 outline-none transition-all" />
                  </div>
                </div>
              </>
            )}
            
            <ConfigInput label={type === 0 ? "Source Root" : "Source Sub-folder"} value={form.sourcePath} onChange={v => setForm({...form, sourcePath: v})} placeholder={type === 0 ? "C:\\Data" : "Production\\Files"} />
            <div className="grid grid-cols-2 gap-4">
              <ConfigInput label="Archive Folder" value={form.archivePath} onChange={v => setForm({...form, archivePath: v})} placeholder="Archive" />
              <ConfigInput label="Suspect Folder" value={form.suspectPath} onChange={v => setForm({...form, suspectPath: v})} placeholder="Suspects" />
            </div>
          </div>

          {error && <div className="p-4 bg-red-500/10 border border-red-500/30 rounded-2xl text-red-400 text-sm font-bold text-center">{error}</div>}

          <button onClick={handleConnect} disabled={loading} className="w-full bg-cyan-600 hover:bg-cyan-500 disabled:bg-slate-800 text-white py-5 rounded-3xl font-black uppercase tracking-widest shadow-lg transition-all flex items-center justify-center gap-3">
            {loading ? <div className="w-6 h-6 border-4 border-white/30 border-t-white rounded-full animate-spin"></div> : 'Initialize Connection'}
          </button>
        </div>
      </div>
    </div>
  );
}

function TabButton({ active, onClick, icon, label }) {
  return (
    <button onClick={onClick} className={`flex items-center gap-2 px-4 py-2 rounded-md font-bold text-sm transition-all ${active ? 'bg-cyan-600 text-white shadow-lg' : 'text-slate-500 hover:text-slate-300 hover:bg-slate-900'}`}>{icon} {label}</button>
  );
}

function MetricCard({ label, value, color = "text-slate-200" }) {
  return (
    <div className="bg-slate-950 p-4 rounded-2xl border border-slate-800 shadow-inner">
      <p className="text-[10px] font-bold text-slate-600 uppercase tracking-widest mb-1">{label}</p>
      <p className={`text-xl font-mono ${color} truncate`}>{value}</p>
    </div>
  );
}

function ConfigInput({ label, value, onChange, placeholder }) {
  return (
    <div>
      <label className="text-xs font-bold text-slate-500 uppercase tracking-widest block mb-2">{label}</label>
      <input value={value} onChange={e => onChange(e.target.value)} placeholder={placeholder} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-2xl text-sm font-mono focus:border-cyan-500 outline-none transition-all placeholder:text-slate-800" />
    </div>
  );
}

export default App;
