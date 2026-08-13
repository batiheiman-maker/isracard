import { Navigate, NavLink, Route, Routes } from "react-router-dom";
import { AddTransactionPage } from "./pages/AddTransactionPage";
import { MonitorPage } from "./pages/MonitorPage";

export default function App() {
  return (
    <div className="app-shell">
      <nav className="app-nav">
        <span className="app-title">Real-Time Financial Monitor</span>
        <div className="nav-links">
          <NavLink to="/add" className={({ isActive }) => (isActive ? "nav-link nav-link-active" : "nav-link")}>
            Simulator
          </NavLink>
          <NavLink to="/monitor" className={({ isActive }) => (isActive ? "nav-link nav-link-active" : "nav-link")}>
            Live Dashboard
          </NavLink>
        </div>
      </nav>
      <main>
        <Routes>
          <Route path="/" element={<Navigate to="/monitor" replace />} />
          <Route path="/add" element={<AddTransactionPage />} />
          <Route path="/monitor" element={<MonitorPage />} />
        </Routes>
      </main>
    </div>
  );
}
