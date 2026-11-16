use anyhow::Context;
use opus::{Application, Channels, Encoder};

/// Resample stereo PCM to 48 kHz and emit 10 ms Opus packets.
pub struct AudioPipe {
    opus: Encoder,
    src_rate: f64,
    input: Vec<i16>,
    pos: f64,
    pending: Vec<i16>,
}

impl AudioPipe {
    pub fn new(src_rate: f64) -> anyhow::Result<Self> {
        let rate = if src_rate < 8000.0 { 48000.0 } else { src_rate };
        let mut opus = Encoder::new(48000, Channels::Stereo, Application::LowDelay)
            .map_err(|err| anyhow::anyhow!("opus encoder: {err}"))?;
        opus.set_bitrate(opus::Bitrate::Bits(64_000))
            .map_err(|err| anyhow::anyhow!("opus bitrate: {err}"))?;
        Ok(Self {
            opus,
            src_rate: rate,
            input: Vec::with_capacity(4096),
            pos: 0.0,
            pending: Vec::with_capacity(4096),
        })
    }

    pub fn push(&mut self, interleaved: &[i16]) -> anyhow::Result<Vec<Vec<u8>>> {
        if interleaved.len() < 2 {
            return Ok(Vec::new());
        }
        self.input.extend_from_slice(interleaved);
        let step = self.src_rate / 48000.0;
        let frames = self.input.len() / 2;
        while self.pos + 1.0 < frames as f64 {
            let i = self.pos as usize;
            let frac = (self.pos - i as f64) as f32;
            let l = lerp(self.input[i * 2], self.input[i * 2 + 2], frac);
            let r = lerp(self.input[i * 2 + 1], self.input[i * 2 + 3], frac);
            self.pending.push(l);
            self.pending.push(r);
            self.pos += step;
        }
        let drop_frames = self.pos as usize;
        if drop_frames > 0 {
            let drop = drop_frames * 2;
            if drop <= self.input.len() {
                self.input.drain(..drop);
            } else {
                self.input.clear();
            }
            self.pos -= drop_frames as f64;
        }

        let mut packets = Vec::new();
        while self.pending.len() >= 960 {
            let frame: Vec<i16> = self.pending.drain(..960).collect();
            let mut buf = [0u8; 512];
            let n = self
                .opus
                .encode(&frame, &mut buf)
                .map_err(|err| anyhow::anyhow!("opus encode: {err}"))
                .context("10ms frame")?;
            packets.push(buf[..n].to_vec());
        }
        Ok(packets)
    }
}

fn lerp(a: i16, b: i16, t: f32) -> i16 {
    let v = a as f32 + (b as f32 - a as f32) * t;
    v.clamp(-32768.0, 32767.0) as i16
}
