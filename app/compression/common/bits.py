class _BitWriter:
    def __init__(self):
        self.out, self.acc, self.bits = bytearray(), 0, 0

    def put(self, value, width):
        self.acc = (self.acc << width) | value
        self.bits += width
        while self.bits >= 8:
            self.bits -= 8
            self.out.append((self.acc >> self.bits) & 255)
        self.acc &= (1 << self.bits) - 1

    def gamma(self, value):
        width = value.bit_length()
        self.put(0, width - 1)
        self.put(value, width)

    def finish(self):
        if self.bits:
            self.out.append(self.acc << (8 - self.bits))
        return bytes(self.out)


class _BitReader:
    def __init__(self, data):
        self.data, self.pos = data, 0

    def get(self, width):
        if self.pos + width > len(self.data) * 8:
            raise ValueError('Оборванный битовый поток')
        value = 0
        for _ in range(width):
            value = (value << 1) | ((self.data[self.pos // 8] >> (7 - self.pos % 8)) & 1)
            self.pos += 1
        return value

    def gamma(self, max_width):
        zeros = 0
        while not self.get(1):
            zeros += 1
            if zeros >= max_width:
                raise ValueError('Слишком длинный целочисленный код')
        return (1 << zeros) | self.get(zeros)

    def finish(self):
        remaining = len(self.data) * 8 - self.pos
        if remaining > 7 or self.get(remaining):
            raise ValueError('Лишние данные или ненулевое заполнение')