import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import ChildWindow from "./ChildWindow";
import "./index.css";

// One SPA: child windows load the same app with hash #/child
const isChildWindow = window.location.hash === "#/child";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {isChildWindow ? <ChildWindow /> : <App />}
  </StrictMode>
);
