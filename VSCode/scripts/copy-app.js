// Copies the dashboard app (Altruist/Dashboard/App/dist, built with `npm run build` there) into media/app, where the
// webview loads it from. Runs before packaging (vscode:prepublish).
const fs = require("fs");
const path = require("path");

const source = path.resolve(__dirname, "..", "..", "Altruist", "Dashboard", "App", "dist");
const target = path.resolve(__dirname, "..", "media", "app");
if (!fs.existsSync(path.join(source, "index.html"))) {
  console.error(`No built dashboard app at ${source}: run "npm run build" in Altruist/Dashboard/App first.`);
  process.exit(1);
}
fs.rmSync(target, { recursive: true, force: true });
fs.cpSync(source, target, { recursive: true });
console.log(`Copied the dashboard app to ${target}.`);
