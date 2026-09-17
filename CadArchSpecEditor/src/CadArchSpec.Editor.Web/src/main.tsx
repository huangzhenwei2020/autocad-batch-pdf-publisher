import React from "react";
import ReactDOM from "react-dom/client";
import "./styles.css";

const cadTableMode = new URLSearchParams(window.location.search).get("mode") === "cad-table";
const App = React.lazy(() => cadTableMode
  ? import("./cad-table-app").then((module) => ({ default: module.CadTableApp }))
  : import("./editor-app").then((module) => ({ default: module.ArchitectureSpecEditor })));

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <React.Suspense fallback={<main className="cad-table-entry-loading">正在加载…</main>}><App /></React.Suspense>
  </React.StrictMode>,
);
