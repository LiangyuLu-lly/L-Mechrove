import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import { Hud } from "./hud/Hud";

const isHud =
  new URLSearchParams(window.location.search).get("window") === "hud";

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    {isHud ? <Hud /> : <App />}
  </React.StrictMode>,
);
