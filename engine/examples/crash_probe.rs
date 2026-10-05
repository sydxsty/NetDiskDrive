use overlaydisk_core::Volume;
use std::{io::Write, path::Path};

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = std::env::args().collect();
    if args.len() != 3 {
        return Err("usage: crash_probe create|write-loop|verify DIRECTORY".into());
    }
    let path = Path::new(&args[2]);
    match args[1].as_str() {
        "create" => {
            Volume::create(path, 64 << 20, None)?;
            println!("CREATED");
        }
        "write-loop" => {
            let volume = Volume::open(path, None)?;
            for generation in 1..=u64::MAX {
                let mut data = vec![0; 256 * 1024];
                for chunk in data.chunks_exact_mut(8) {
                    chunk.copy_from_slice(&generation.to_le_bytes());
                }
                volume.write(0, &data)?;
                println!("COMMITTED {generation}");
                std::io::stdout().flush()?;
                std::thread::sleep(std::time::Duration::from_millis(20));
            }
        }
        "verify" => {
            let volume = Volume::open(path, None)?;
            let mut data = vec![0; 256 * 1024];
            volume.read(0, &mut data)?;
            let generation = u64::from_le_bytes(data[..8].try_into()?);
            if !data.chunks_exact(8).all(|c| c == generation.to_le_bytes()) {
                return Err("TORN GENERATION".into());
            }
            println!("VERIFIED {generation}");
        }
        _ => return Err("unknown command".into()),
    }
    Ok(())
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
