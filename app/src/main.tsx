import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { Hud } from "./hud/Hud";
import { CustomModeWindow } from "./sections/CustomModeWindow";
import { PERFORMANCE_MODES } from "./lib/types";

// Dev-only render tooling. Both imports are gated so nothing ships to the
// production bundle, and neither loads a remote script into the webview.
if (import.meta.env.DEV) {
  void import("react-grab");
  void import("react-scan").then(({ scan }) => {
    scan({ enabled: true });
  });
}

const windowKind = new URLSearchParams(window.location.search).get("window");
const rawMode = new URLSearchParams(window.location.search).get("mode");
const editorMode = PERFORMANCE_MODES.find((m) => m === rawMode);

function Root() {
  if (windowKind === "hud") {
    return <Hud />;
  }
  if (windowKind === "custom") {
    return <CustomModeWindow mode={editorMode} />;
  }
  return <App />;
}

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <Root />
  </React.StrictMode>,
);
