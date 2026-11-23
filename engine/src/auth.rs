//! Shared session tokens and per-user save paths.
//!
//! The web app signs tokens with the secret in `{data}/auth.secret`. The engine
//! checks them on every request except `/api/health`. A missing or bad token is
//! rejected, so the process on port 9090 is not an anonymous play endpoint.

use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Clone, Debug)]
pub struct AuthContext {
    pub user_id: String,
    pub role: String,
}

pub fn load_secret(data: &Path) -> String {
    std::fs::read_to_string(data.join("auth.secret"))
        .unwrap_or_default()
        .trim()
        .to_string()
}

pub fn verify_token(secret: &str, token: &str) -> Option<AuthContext> {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    verify_token_at(secret, token, now)
}

pub fn verify_token_at(secret: &str, token: &str, now: u64) -> Option<AuthContext> {
    if secret.is_empty() {
        return None;
    }
    let (body, mac) = token.rsplit_once('.')?;
    let expected = hmac_sha256(secret.as_bytes(), body.as_bytes());
    let given = b64url_decode(mac)?;
    if given.len() != expected.len() || !constant_eq(&given, &expected) {
        return None;
    }
    let payload = b64url_decode(body.strip_prefix("v1.")?)?;
    let payload = std::str::from_utf8(&payload).ok()?;
    let sub = json_string(payload, "sub")?;
    let role = json_string(payload, "role")?;
    let exp = json_u64(payload, "exp")?;
    if exp.saturating_add(30) < now {
        return None;
    }
    if !safe_id(&sub) || !safe_id(&role) {
        return None;
    }
    Some(AuthContext { user_id: sub, role })
}

pub fn progress_dir(data: &Path, user_id: &str, system: &str, game_hash: &str) -> PathBuf {
    data.join("saves")
        .join(sanitize(user_id))
        .join(sanitize(system))
        .join(sanitize(game_hash))
}

pub fn slot_file(dir: &Path, slot: &str) -> PathBuf {
    let name = match slot {
        "auto" => "auto.state".to_string(),
        "1" | "2" | "3" | "4" | "5" => format!("slot-{slot}.state"),
        _ => "slot-1.state".to_string(),
    };
    dir.join(name)
}

pub fn sha256_file(path: &Path) -> std::io::Result<String> {
    let bytes = std::fs::read(path)?;
    Ok(hex(&sha256(&bytes)))
}

fn sanitize(value: &str) -> String {
    let cleaned: String = value
        .chars()
        .filter(|ch| ch.is_ascii_alphanumeric() || *ch == '-')
        .take(80)
        .collect();
    if cleaned.is_empty() {
        "unknown".into()
    } else {
        cleaned
    }
}

fn safe_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 80
        && value
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '-')
}

fn json_string(json: &str, key: &str) -> Option<String> {
    let needle = format!("\"{key}\":\"");
    let start = json.find(&needle)? + needle.len();
    let rest = &json[start..];
    let end = rest.find('"')?;
    Some(rest[..end].to_string())
}

fn json_u64(json: &str, key: &str) -> Option<u64> {
    let needle = format!("\"{key}\":");
    let start = json.find(&needle)? + needle.len();
    let rest = json[start..].trim_start();
    let digits: String = rest.chars().take_while(|ch| ch.is_ascii_digit()).collect();
    digits.parse().ok()
}

fn constant_eq(a: &[u8], b: &[u8]) -> bool {
    let mut diff = a.len() ^ b.len();
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= (*x ^ *y) as usize;
    }
    diff == 0
}

fn hex(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        out.push(HEX[(byte >> 4) as usize] as char);
        out.push(HEX[(byte & 0xf) as usize] as char);
    }
    out
}

pub fn b64url_decode(input: &str) -> Option<Vec<u8>> {
    let mut s = input.replace('-', "+").replace('_', "/");
    while s.len() % 4 != 0 {
        s.push('=');
    }
    fn val(c: u8) -> Option<u8> {
        match c {
            b'A'..=b'Z' => Some(c - b'A'),
            b'a'..=b'z' => Some(c - b'a' + 26),
            b'0'..=b'9' => Some(c - b'0' + 52),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    }
    let bytes = s.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i + 4 <= bytes.len() {
        if bytes[i] == b'=' {
            break;
        }
        let a = val(bytes[i])?;
        let b = val(bytes[i + 1])?;
        let c = if bytes[i + 2] == b'=' {
            0
        } else {
            val(bytes[i + 2])?
        };
        let d = if bytes[i + 3] == b'=' {
            0
        } else {
            val(bytes[i + 3])?
        };
        out.push((a << 2) | (b >> 4));
        if bytes[i + 2] != b'=' {
            out.push((b << 4) | (c >> 2));
        }
        if bytes[i + 3] != b'=' {
            out.push((c << 6) | d);
        }
        i += 4;
    }
    Some(out)
}

pub fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    let mut key_block = [0u8; 64];
    if key.len() > 64 {
        let hashed = sha256(key);
        key_block[..32].copy_from_slice(&hashed);
    } else {
        key_block[..key.len()].copy_from_slice(key);
    }
    let mut inner = Vec::with_capacity(64 + message.len());
    let mut outer_key = [0u8; 64];
    for i in 0..64 {
        inner.push(key_block[i] ^ 0x36);
        outer_key[i] = key_block[i] ^ 0x5c;
    }
    inner.extend_from_slice(message);
    let inner_hash = sha256(&inner);
    let mut outer = Vec::with_capacity(96);
    outer.extend_from_slice(&outer_key);
    outer.extend_from_slice(&inner_hash);
    sha256(&outer)
}

pub fn sha256(message: &[u8]) -> [u8; 32] {
    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
        0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
        0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
        0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
        0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
        0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
        0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];
    let mut h: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];
    let bit_len = (message.len() as u64).saturating_mul(8);
    let mut data = message.to_vec();
    data.push(0x80);
    while data.len() % 64 != 56 {
        data.push(0);
    }
    data.extend_from_slice(&bit_len.to_be_bytes());
    for chunk in data.chunks_exact(64) {
        let mut w = [0u32; 64];
        for i in 0..16 {
            w[i] = u32::from_be_bytes(chunk[i * 4..i * 4 + 4].try_into().unwrap());
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16]
                .wrapping_add(s0)
                .wrapping_add(w[i - 7])
                .wrapping_add(s1);
        }
        let mut a: u32 = h[0];
        let mut b: u32 = h[1];
        let mut c: u32 = h[2];
        let mut d: u32 = h[3];
        let mut e: u32 = h[4];
        let mut f: u32 = h[5];
        let mut g: u32 = h[6];
        let mut hh: u32 = h[7];
        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let t1 = hh
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(K[i])
                .wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let t2 = s0.wrapping_add(maj);
            hh = g;
            g = f;
            f = e;
            e = d.wrapping_add(t1);
            d = c;
            c = b;
            b = a;
            a = t1.wrapping_add(t2);
        }
        h[0] = h[0].wrapping_add(a);
        h[1] = h[1].wrapping_add(b);
        h[2] = h[2].wrapping_add(c);
        h[3] = h[3].wrapping_add(d);
        h[4] = h[4].wrapping_add(e);
        h[5] = h[5].wrapping_add(f);
        h[6] = h[6].wrapping_add(g);
        h[7] = h[7].wrapping_add(hh);
    }
    let mut out = [0u8; 32];
    for (i, value) in h.iter().enumerate() {
        out[i * 4..i * 4 + 4].copy_from_slice(&value.to_be_bytes());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sha256_abc() {
        let digest = hex(&sha256(b"abc"));
        assert_eq!(
            digest,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn hmac_rfc4231_case_1() {
        let key = [0x0bu8; 20];
        let mac = hex(&hmac_sha256(&key, b"Hi There"));
        assert_eq!(
            mac,
            "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"
        );
    }

    #[test]
    fn two_users_do_not_share_a_save_directory() {
        let root = Path::new("/data");
        let a = progress_dir(root, "user-a", "nes", "abc123");
        let b = progress_dir(root, "user-b", "nes", "abc123");
        assert_ne!(a, b);
        assert!(a.ends_with("user-a/nes/abc123") || a.ends_with("user-a\\nes\\abc123"));
    }

    #[test]
    fn token_round_trip_and_rejection() {
        let secret = "test-secret";
        let payload = b"{\"sub\":\"user-a\",\"exp\":200,\"role\":\"User\"}";
        let body = format!("v1.{}", b64url(payload));
        let mac = b64url(&hmac_sha256(secret.as_bytes(), body.as_bytes()));
        let token = format!("{body}.{mac}");
        let ok = verify_token_at(secret, &token, 100).unwrap();
        assert_eq!(ok.user_id, "user-a");
        assert!(verify_token_at(secret, &token, 300).is_none());
        assert!(verify_token_at("other", &token, 100).is_none());
    }

    fn b64url(data: &[u8]) -> String {
        const T: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        let mut out = String::new();
        let mut i = 0;
        while i + 3 <= data.len() {
            let n = ((data[i] as u32) << 16) | ((data[i + 1] as u32) << 8) | data[i + 2] as u32;
            out.push(T[((n >> 18) & 63) as usize] as char);
            out.push(T[((n >> 12) & 63) as usize] as char);
            out.push(T[((n >> 6) & 63) as usize] as char);
            out.push(T[(n & 63) as usize] as char);
            i += 3;
        }
        if i < data.len() {
            let mut n = (data[i] as u32) << 16;
            out.push(T[((n >> 18) & 63) as usize] as char);
            if i + 1 < data.len() {
                n |= (data[i + 1] as u32) << 8;
                out.push(T[((n >> 12) & 63) as usize] as char);
                out.push(T[((n >> 6) & 63) as usize] as char);
            } else {
                out.push(T[((n >> 12) & 63) as usize] as char);
            }
        }
        out
    }
}
