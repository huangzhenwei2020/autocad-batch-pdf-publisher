import React from "react";
import ReactDOM from "react-dom/client";
import { ArchitectureSpecEditor } from "./editor-app";
import "./styles.css";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <ArchitectureSpecEditor />
  </React.StrictMode>,
);
