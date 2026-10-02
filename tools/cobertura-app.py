"""Cobertura de lineas sobre TODA la aplicacion (constitucion General 8.6).

uso: python tools/cobertura-app.py <carpeta-de-resultados | coverage.cobertura.xml> [--app <carpeta>]
       [--excluir <dir> ...] [--detalle] [--calibrar]

Como se cuenta (las mismas reglas para todos los ficheros .cs de la app):
  - Ficheros: todos los .cs bajo la carpeta de la app (por defecto, la actual), fuera de obj/, bin/,
    *.g.cs, *.Designer.cs, los proyectos de pruebas (*.Tests, *.UITests) y lo que se pase con
    --excluir.
  - Solo cuentan las lineas con SENTENCIAS (lo que coverlet llama puntos de secuencia). No cuentan:
    lineas vacias, comentarios, directivas (#if), llaves y parentesis sueltos, using/namespace,
    atributos [..] (tambien los partidos en varias lineas), constantes (tambien partidas en varias
    lineas), campos sin inicializar, firmas de metodos y propiedades (incluidas sus
    listas de parametros partidas en varias lineas), `else`, `try`, `finally`, `case X:`, y todo lo
    que hay dentro de las interfaces y de los enum.
  - Fichero que compila el banco de pruebas (sale en el informe de coverlet): sus lineas ejecutables
    son las que marca coverlet, quitando las de llaves sueltas, y una linea esta cubierta si coverlet
    la ha visto ejecutarse. coverlet se ejecuta SIN excluir CompilerGeneratedAttribute, asi que los
    metodos async y las lambdas cuentan.
  - Fichero que el banco no compila: se cuentan sus sentencias con las reglas de arriba y todas
    cuentan como NO cubiertas. Lo que no se prueba, baja el porcentaje.
  --calibrar compara, en los ficheros instrumentados, el recuento de estas reglas con lo que marca
  coverlet, para ver que la estimacion de lo no instrumentado se parece a lo que contaria coverlet.
  --detalle lista los ficheros de mas a menos lineas sin cubrir.
"""
import os, re, sys, glob, xml.etree.ElementTree as ET

args = sys.argv[1:]
if not args or args[0].startswith('--'):
    print(__doc__); sys.exit(1)
src = args[0]
app = os.getcwd()
excl = []
detalle = '--detalle' in args
calibrar = '--calibrar' in args
if '--app' in args:
    app = args[args.index('--app') + 1]
if '--excluir' in args:
    i = args.index('--excluir') + 1
    while i < len(args) and not args[i].startswith('--'):
        excl.append(args[i]); i += 1
app = os.path.abspath(app)


def norm(p):
    return os.path.normcase(os.path.normpath(p))


excl = [norm(os.path.join(app, e)) for e in excl]
xmls = [src] if src.endswith('.xml') else glob.glob(os.path.join(src, '**', 'coverage.cobertura.xml'), recursive=True)
if not xmls:
    sys.exit('No hay coverage.cobertura.xml en ' + src)

hits = {}  # fichero -> {linea: hits}
for x in xmls:
    root = ET.parse(x).getroot()
    sources = [s.text or '' for s in root.iter('source')]
    for cls in root.iter('class'):
        fn = cls.get('filename')
        full = fn if os.path.isabs(fn) else next(
            (os.path.join(s, fn) for s in sources if os.path.exists(os.path.join(s, fn))), fn)
        d = hits.setdefault(norm(full), {})
        for ln in cls.iter('line'):
            n = int(ln.get('number')); h = int(ln.get('hits'))
            d[n] = max(d.get(n, 0), h)

MODS = r'(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|new|readonly|unsafe|partial|required|volatile|event|implicit|explicit|file)\s+)'
LONE = re.compile(r'^[{}()\[\];,]*$')
NOSTMT = re.compile(r'^(?:else|try|finally|do|unchecked|checked|unsafe|default:|case\b.*:|get|set|init|add|remove)$')
TYPEDECL = re.compile(r'^' + MODS + r'*(?:readonly\s+|ref\s+)*(class|struct|record|interface|enum)\b')
KEYWORD = re.compile(r'^(?:if|for|foreach|while|switch|using|lock|return|await|var|throw|yield|fixed|break|continue|goto|else\s+if|catch)\b')
USING = re.compile(r'^(?:global\s+)?using\s+(?:static\s+)?[\w.=\s<>,]+;$')
CONST = re.compile(r'^' + MODS + r'*const\s')
FIELD = re.compile(r'^' + MODS + r'*[\w<>\[\],.?]+\s+\w+\s*;$')
CTOR = re.compile(r'^' + MODS + r'*(\w+)\s*\(')


def limpiar(lines):
    """Devuelve (numero, codigo) sin comentarios ni cadenas."""
    blk = False
    for i, raw in enumerate(lines, 1):
        s = raw.strip()
        if blk:
            if '*/' not in s:
                continue
            blk = False; s = s.split('*/', 1)[1].strip()
        code = re.sub(r'@?\$?"(?:\\.|[^"\\])*"', '""', s)
        code = re.sub(r"'(?:\\.|[^'\\])'", "''", code)
        if '/*' in code and '//' not in code.split('/*', 1)[0]:
            head, tail = code.split('/*', 1)
            if '*/' in tail:
                code = (head + tail.split('*/', 1)[1]).strip()
            else:
                blk = True; code = head.strip()
        if '//' in code:
            code = code.split('//', 1)[0].strip()
        yield i, code


def ejecutables(path):
    """Numeros de linea que estas reglas consideran sentencias."""
    out = set()
    lines = open(path, encoding='utf-8-sig', errors='replace').read().split('\n')
    stack = []            # por cada '{' abierta: 'iface', 'enum' u 'other'
    pending = None        # tipo de la ultima declaracion de tipo, a la espera de su '{'
    sig_depth = 0         # >0: dentro de los parametros de una firma partida en varias lineas
    attr_depth = 0        # >0: dentro de un atributo [..] partido en varias lineas
    in_const = False      # continuacion de una constante partida en varias lineas
    cls_name = None
    for i, code in limpiar(lines):
        if not code or code.startswith('#'):
            continue
        if attr_depth > 0:
            attr_depth += code.count('[') - code.count(']')
            continue
        if in_const:
            in_const = not code.endswith(';')
            continue
        if code.startswith('[') and code.count('[') > code.count(']'):
            attr_depth = code.count('[') - code.count(']')
            continue
        inside = stack[-1] if stack else 'other'
        m = TYPEDECL.match(code)
        stmt = True
        if sig_depth > 0:
            stmt = False
            sig_depth += code.count('(') - code.count(')')
        elif inside in ('iface', 'enum'):
            stmt = False
        elif LONE.match(code) or NOSTMT.match(code):
            stmt = False
        elif USING.match(code) or code.startswith('namespace '):
            stmt = False
        elif code.startswith('[') and code.endswith(']'):
            stmt = False
        elif m:
            nm = re.search(r'\b(?:class|struct|record)\s+(\w+)', code)
            if nm:
                cls_name = nm.group(1)
            # los record posicionales y los constructores primarios generan codigo: coverlet los marca
            # (sus parametros, si siguen en otras lineas, tambien)
            stmt = '(' in code and m.group(1) != 'interface'
        elif CONST.match(code):
            stmt = False
            in_const = not code.endswith(';')
        elif KEYWORD.match(code):
            stmt = True
        elif re.match(r'^' + MODS + r'+[^=(]*=(?!>)', code):
            stmt = True   # campo o propiedad con inicializador (aunque siga en otras lineas)
        elif re.match(r'^' + MODS + r'+', code) and not code.endswith(';') \
                and not re.search(r'=>\s*\S', code) and not re.search(r'\{.*\S.*\}', code):
            # firma de metodo/constructor/propiedad con el cuerpo debajo; la del constructor cuenta
            # (coverlet marca su linea por la llamada implicita a base())
            c = CTOR.match(code)
            stmt = bool(c) and c.group(1) == cls_name
            if code.count('(') > code.count(')'):
                sig_depth = code.count('(') - code.count(')')
        elif FIELD.match(code) and '=' not in code:
            stmt = False
        if stmt:
            out.add(i)
        if m:
            pending = {'interface': 'iface', 'enum': 'enum'}.get(m.group(1), 'other')
        for ch in code:
            if ch == '{':
                if pending is not None:
                    stack.append(pending); pending = None
                else:
                    stack.append(inside if inside in ('iface', 'enum') else 'other')
            elif ch == '}' and stack:
                stack.pop()
        if m and code.endswith(';'):
            pending = None
    return out


files = []
for dp, dns, fns in os.walk(app):
    dns[:] = [d for d in dns if d.lower() not in ('obj', 'bin', '.git', '.vs', 'node_modules', 'constitution', 'testresults')
              and not d.lower().endswith('.tests') and not d.lower().endswith('.uitests')]
    if any(norm(dp) == e or norm(dp).startswith(e + os.sep) for e in excl):
        continue
    for f in fns:
        if f.endswith('.cs') and not f.endswith('.g.cs') and not f.endswith('.Designer.cs'):
            files.append(os.path.join(dp, f))

tot = cub = tot_i = cub_i = cal_est = cal_cov = 0
rows = []
for f in sorted(files):
    h = hits.get(norm(f))
    if h is not None:
        txt = open(f, encoding='utf-8-sig', errors='replace').read().split('\n')
        ejec = {n for n in h if n <= len(txt) and not LONE.match(txt[n - 1].strip())}
        c = sum(1 for n in ejec if h[n] > 0)
        tot_i += len(ejec); cub_i += c
        if calibrar:
            cal_est += len(ejecutables(f)); cal_cov += len(ejec)
        n = len(ejec)
    else:
        n = len(ejecutables(f)); c = 0
    tot += n; cub += c
    rows.append((os.path.relpath(f, app), n, c, h is not None))

if detalle:
    for r, n, c, ins in sorted(rows, key=lambda r: r[1] - r[2], reverse=True):
        if n - c:
            print(f'{n - c:6d} sin cubrir  {c:5d}/{n:<5d} {"I" if ins else "-"} {r}')
if calibrar and cal_cov:
    print(f'Calibracion: en lo instrumentado, estas reglas cuentan {cal_est} lineas y coverlet {cal_cov} '
          f'({(cal_est - cal_cov) / cal_cov * 100:+.1f} %)')
print(f'Instrumentado: {cub_i} de {tot_i} lineas ({cub_i / tot_i * 100 if tot_i else 0:.1f} %)')
print(f'Toda la app:   {cub} de {tot} lineas ejecutables ({cub / tot * 100 if tot else 0:.1f} %) en {len(files)} ficheros .cs')
