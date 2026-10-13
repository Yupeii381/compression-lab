from app.compression.limits import MAX_BLOCK_OUTPUT

def encode(data, min_run=3):
    out = bytearray(b"RL1")
    i = 0
    while i < len(data):
        run = 1
        while run < 128 and i + run < len(data) and data[i + run] == data[i]:
            run += 1
        if run >= min_run:
            out.extend((127 + run, data[i]))
            i += run
            continue
        start = i
        i += 1
        while i - start < 128 and i < len(data):
            run = 1
            while run < min_run and i + run < len(data) and data[i + run] == data[i]:
                run += 1
            if run >= min_run:
                break
            i += 1
        out.append(i - start - 1)
        out.extend(data[start:i])
    return bytes(out)


def decode(data, *, min_run=3):
    if data[:3] != b"RL1":
        raise ValueError("Неверный заголовок RLE")
    out, i = bytearray(), 3
    while i < len(data):
        tag = data[i]
        i += 1
        if tag < 128:
            n = tag + 1
            if i + n > len(data):
                raise ValueError("Оборванный блок RLE")
            out.extend(data[i:i+n])
            i += n
        else:
            if i >= len(data):
                raise ValueError("Оборванная серия RLE")
            out.extend(bytes([data[i]]) * (tag - 127))
            i += 1
        if len(out) > MAX_BLOCK_OUTPUT:
            raise ValueError("Превышен размер блока")
    return bytes(out)