// STRICT, bounded JSON frame parser for OFFLINE Authority V1 contract.
// Rejects duplicate keys at all nested levels, including equivalent \uXXXX
// escaped field names. NOT an HTTP, TLS, device identity, or send adapter.
export function parseUniqueJsonFrame(raw, maxUtf8Bytes=4096, maxDepth=12) {
  if(typeof raw!=='string'||typeof maxUtf8Bytes!=='number'||
    !Number.isSafeInteger(maxUtf8Bytes)||maxUtf8Bytes<1||
    !Number.isSafeInteger(maxDepth)||maxDepth<1||
    Buffer.byteLength(raw,'utf8')>maxUtf8Bytes) {
    throw new SyntaxError('invalid JSON frame bounds');
  }
  let pos=0;
  const len=raw.length;
  function skip(){while(pos<len && /[\u0020\u0009\u000a\u000d]/.test(raw[pos]))pos++;}
  function expect(token){
    if(raw.slice(pos,pos+token.length)!==token)
      throw new SyntaxError('unexpected JSON token');
    pos+=token.length;
  }
  function parseString(){
    if(raw[pos]!=='"')throw new SyntaxError('string required');
    const start=pos++;
    while(pos<len){
      const c=raw.charCodeAt(pos++);
      if(c===34) {
        const encoded=raw.slice(start,pos);
        return JSON.parse(encoded);
      }
      if(c<0x20)throw new SyntaxError('unescaped control in JSON string');
      if(c===92){
        if(pos>=len)throw new SyntaxError('unfinished escape');
        const e=raw[pos++];
        if(e==='u'){
          if(!/^[0-9a-fA-F]{4}$/.test(raw.slice(pos,pos+4)))
            throw new SyntaxError('bad unicode escape');
          pos+=4;
        }else if(!'"/\\bfnrt'.includes(e)){
          throw new SyntaxError('bad JSON escape');
        }
      }
    }
    throw new SyntaxError('unterminated JSON string');
  }
  function parseValue(depth){
    skip();
    if(depth>maxDepth)throw new SyntaxError('JSON depth exceeded');
    if(pos>=len)throw new SyntaxError('missing JSON value');
    const c=raw[pos];
    if(c==='"') {parseString();return;}
    if(c==='{'){
      pos++;skip();
      if(raw[pos]==='}'){pos++;return;}
      const names=new Set();
      while(true){
        skip();
        const key=parseString();
        if(names.has(key))throw new SyntaxError('duplicate JSON key');
        names.add(key);
        skip();expect(':');
        parseValue(depth+1);skip();
        if(raw[pos]==='}'){pos++;return;}
        expect(',');
      }
    }
    if(c==='['){
      pos++;skip();
      if(raw[pos]===']'){pos++;return;}
      while(true){
        parseValue(depth+1);skip();
        if(raw[pos]===']'){pos++;return;}
        expect(',');
      }
    }
    for(const word of ['true','false','null']){
      if(raw.startsWith(word,pos)){pos+=word.length;return;}
    }
    if(c==='-'||(c>='0'&&c<='9')){
      const m=/^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(raw.slice(pos));
      if(!m)throw new SyntaxError('invalid JSON number');
      pos+=m[0].length;
      return;
    }
    throw new SyntaxError('invalid JSON value');
  }
  parseValue(0);
  skip();
  if(pos!==len)throw new SyntaxError('trailing JSON input');
  return JSON.parse(raw);
}
