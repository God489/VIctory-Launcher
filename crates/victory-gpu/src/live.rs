//! Diagnostics: the live link. A running title is changed and read from outside, without
//! a restart. `VICTORY_LIVE=<folder>` (the launcher sets it):
//!
//! - at a present, at most twice a second, `<folder>\<pid>.cmd` is read again when it
//!   changed: one command a line, `set NAME=VALUE` or `unset NAME`. A value set so wins
//!   over the variable: the switches [`crate::ab`] reads (`ab::pick`), the light stand-in's
//!   level and air (`VICTORY_AMBIENT`, `VICTORY_AMBIENT_AIR`);
//! - every second `<folder>\<pid>.json` is written: that second's frames (mean, median,
//!   nine tenths, in ms), draws a frame, and the values set.
//!
//! A file and not a pipe: any tool can write it (the launcher, a script, an editor), and a
//! title that stands still for a while (a stall, a breakpoint) loses nothing.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};
use std::time::{Instant, SystemTime};

fn folder() -> Option<&'static PathBuf> {
    static FOLDER: OnceLock<Option<PathBuf>> = OnceLock::new();
    FOLDER
        .get_or_init(|| {
            let dir = PathBuf::from(std::env::var_os("VICTORY_LIVE").filter(|v| !v.is_empty())?);
            std::fs::create_dir_all(&dir).ok()?;
            tracing::info!(folder = %dir.display(), pid = std::process::id(), "live link: commands in <pid>.cmd, state in <pid>.json");
            Some(dir)
        })
        .as_ref()
}

/// The values set from outside, by name.
static VALUES: Mutex<BTreeMap<String, String>> = Mutex::new(BTreeMap::new());

/// The value set for `name`, if the live link set one.
pub(crate) fn value(name: &str) -> Option<String> {
    folder()?;
    VALUES.lock().unwrap_or_else(|e| e.into_inner()).get(name).cloned()
}

/// `name` set as a switch: `0`, `off` and `false` are off, anything else on.
pub(crate) fn flag(name: &str) -> Option<bool> {
    value(name).map(|v| !matches!(v.trim(), "" | "0" | "off" | "false"))
}

/// Applies the commands of `text`; how many took.
fn apply(text: &str) -> usize {
    let mut values = VALUES.lock().unwrap_or_else(|e| e.into_inner());
    let mut took = 0;
    for line in text.lines().map(str::trim).filter(|l| !l.is_empty() && !l.starts_with('#')) {
        let (verb, rest) = line.split_once(char::is_whitespace).unwrap_or((line, ""));
        match verb {
            "set" => {
                if let Some((name, value)) = rest.split_once('=')
                    && name.trim().starts_with("VICTORY_")
                {
                    let (name, value) = (name.trim().to_string(), value.trim().to_string());
                    if values.get(&name) != Some(&value) {
                        tracing::info!(%name, %value, "live link: set");
                    }
                    values.insert(name, value);
                    took += 1;
                }
            }
            "unset" => {
                if values.remove(rest.trim()).is_some() {
                    tracing::info!(name = rest.trim(), "live link: unset");
                }
                took += 1;
            }
            _ => tracing::warn!(line, "live link: not a command (set NAME=VALUE, unset NAME)"),
        }
    }
    took
}

struct State {
    checked: Instant,
    modified: Option<SystemTime>,
    second: Instant,
    last: Option<Instant>,
    frames: Vec<f32>,
    drawn: u64,
}

/// The title's present ended (on the presenting thread).
pub(crate) fn presented() {
    let Some(dir) = folder() else {
        return;
    };
    static STATE: Mutex<Option<State>> = Mutex::new(None);
    let now = Instant::now();
    let drawn = crate::draw::drawn();
    let mut state = STATE.lock().unwrap_or_else(|e| e.into_inner());
    let s = state.get_or_insert_with(|| State { checked: now, modified: None, second: now, last: None, frames: Vec::new(), drawn });
    if let Some(last) = s.last {
        s.frames.push((now - last).as_secs_f32() * 1000.0);
    }
    s.last = Some(now);
    let pid = std::process::id();
    if now.duration_since(s.checked).as_millis() >= 500 {
        s.checked = now;
        let cmd = dir.join(format!("{pid}.cmd"));
        let modified = std::fs::metadata(&cmd).and_then(|m| m.modified()).ok();
        if modified.is_some() && modified != s.modified {
            s.modified = modified;
            if let Ok(text) = std::fs::read_to_string(&cmd) {
                // The whole file is the set wanted: what it no longer names goes back.
                VALUES.lock().unwrap_or_else(|e| e.into_inner()).clear();
                apply(&text);
            }
        }
    }
    if now.duration_since(s.second).as_secs_f32() >= 1.0 {
        let mut frames = std::mem::take(&mut s.frames);
        let count = frames.len();
        let draws = if count > 0 { drawn.saturating_sub(s.drawn) as f64 / count as f64 } else { 0.0 };
        let (mean, median, most) = if count == 0 {
            (0.0, 0.0, 0.0)
        } else {
            let mean = frames.iter().sum::<f32>() / count as f32;
            frames.sort_by(f32::total_cmp);
            (mean, frames[count / 2], frames[count * 9 / 10])
        };
        s.second = now;
        s.drawn = drawn;
        let values = VALUES.lock().unwrap_or_else(|e| e.into_inner()).clone();
        let set = values
            .iter()
            .map(|(k, v)| format!("{}:{}", json_string(k), json_string(v)))
            .collect::<Vec<_>>()
            .join(",");
        let unix = SystemTime::now().duration_since(SystemTime::UNIX_EPOCH).map_or(0, |d| d.as_secs());
        let text = format!(
            "{{\"pid\":{pid},\"time\":{unix},\"frames\":{count},\"mean\":{mean:.2},\"median\":{median:.2},\"p90\":{most:.2},\"draws\":{draws:.0},\"set\":{{{set}}}}}"
        );
        let path = dir.join(format!("{pid}.json"));
        let temp = dir.join(format!("{pid}.json.tmp"));
        if std::fs::write(&temp, text).is_ok() {
            let _ = std::fs::rename(&temp, &path);
        }
    }
}

fn json_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn commands_set_and_unset() {
        VALUES.lock().unwrap().clear();
        assert_eq!(apply("set VICTORY_AMBIENT=constant:1.2\n# a note\nset NOT_OURS=1\nunset VICTORY_X\n"), 2);
        assert_eq!(VALUES.lock().unwrap().get("VICTORY_AMBIENT").map(String::as_str), Some("constant:1.2"));
        assert!(!VALUES.lock().unwrap().contains_key("NOT_OURS"));
        apply("unset VICTORY_AMBIENT");
        assert!(VALUES.lock().unwrap().is_empty());
    }

    #[test]
    fn strings_are_escaped() {
        assert_eq!(json_string("a\"b\\c"), "\"a\\\"b\\\\c\"");
    }
}
