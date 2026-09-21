import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { Hud } from "./hud/Hud";
import { CustomModeWindow } from "./sections/CustomModeWindow";

// Dev-only render tooling. Both imports are gated so nothing ships to the
// production bundle, and neither loads a remote script into the webview.
if (import.meta.env.DEV) {
  void import("react-grab");
  void import("react-scan").then(({ scan }) => {
    scan({ enabled: true });
  });
}

const windowKind = new URLSearchParams(window.location.search).get("window");

function Root() {
  if (windowKind === "hud") {
    return <Hud />;
  }
  if (windowKind === "custom") {
    return <CustomModeWindow />;
  }
  return <App />;
}

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <Root />
  </React.StrictMode>,
);
