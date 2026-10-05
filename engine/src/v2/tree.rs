use super::{
    codec::{MetaRef, PageRef},
    Error, Result,
};
#[derive(Clone)]
pub enum Node {
    Leaf(Box<[Option<PageRef>; 32]>),
    Branch(Box<[MetaRef; 64]>),
}
impl Node {
    pub fn empty(level: u8) -> Self {
        if level == 0 {
            Self::Leaf(Box::new([None; 32]))
        } else {
            Self::Branch(Box::new([MetaRef::default(); 64]))
        }
    }
    pub fn encode(&self) -> Vec<u8> {
        match self {
            Self::Leaf(pages) => {
                let mut out = vec![0; 1 + 32 * 72];
                for (i, p) in pages.iter().enumerate() {
                    if let Some(p) = p {
                        p.put(&mut out[1 + i * 72..1 + (i + 1) * 72]);
                    }
                }
                out
            }
            Self::Branch(children) => {
                let mut out = vec![0; 1 + 64 * 40];
                out[0] = 1;
                for (i, p) in children.iter().enumerate() {
                    p.put(&mut out[1 + i * 40..1 + (i + 1) * 40]);
                }
                out
            }
        }
    }
    pub fn decode(input: &[u8]) -> Result<Self> {
        if input.len() == 1 + 32 * 72 && input[0] == 0 {
            let mut pages = Box::new([None; 32]);
            for (i, p) in pages.iter_mut().enumerate() {
                *p = PageRef::get(&input[1 + i * 72..1 + (i + 1) * 72]);
            }
            Ok(Self::Leaf(pages))
        } else if input.len() == 1 + 64 * 40 && input[0] == 1 {
            let mut refs = Box::new([MetaRef::default(); 64]);
            for (i, p) in refs.iter_mut().enumerate() {
                *p = MetaRef::get(&input[1 + i * 40..1 + (i + 1) * 40]);
            }
            Ok(Self::Branch(refs))
        } else {
            Err(Error::Integrity("index node layout".into()))
        }
    }
    pub fn is_empty(&self) -> bool {
        match self {
            Self::Leaf(p) => p.iter().all(Option::is_none),
            Self::Branch(p) => p.iter().all(|r| r.empty()),
        }
    }
}
