#include "Core.h"
#include <algorithm>
#include <bit>
#include <cmath>
#include <limits>
#include <type_traits>

namespace hs2 {
namespace {
void Number(Bytes& output,std::uint64_t number,int width){for(int i=width-1;i>=0;--i)output.push_back(static_cast<std::uint8_t>(number>>(i*8)));}
void Length(Bytes& output,std::size_t length,int one,int two,int four){if(length>UINT32_MAX)throw std::runtime_error("MessagePack 长度超限");int width=length<=255?1:length<=65535?2:4;output.push_back(static_cast<std::uint8_t>(width==1?one:width==2?two:four));Number(output,length,width);}
void Container(Bytes& output,std::size_t count,int fix,int two,int four){if(count<16)output.push_back(static_cast<std::uint8_t>(fix|count));else{output.push_back(static_cast<std::uint8_t>(count<=65535?two:four));Number(output,count,count<=65535?2:4);}}
void Unsigned(Bytes& output,std::uint64_t value){if(value<=127){output.push_back(static_cast<std::uint8_t>(value));return;}int width=value<=255?1:value<=65535?2:value<=UINT32_MAX?4:8;output.push_back(static_cast<std::uint8_t>(width==1?0xcc:width==2?0xcd:width==4?0xce:0xcf));Number(output,value,width);}
bool EqualKey(const MessagePack::Value& a,const MessagePack::Value& b){if(a==b)return true;if(auto p=std::get_if<std::int64_t>(&a))if(auto q=std::get_if<std::uint64_t>(&b))return *p>=0&&static_cast<std::uint64_t>(*p)==*q;if(auto p=std::get_if<std::uint64_t>(&a))if(auto q=std::get_if<std::int64_t>(&b))return *q>=0&&*p==static_cast<std::uint64_t>(*q);return false;}
void ValidateUtf8(std::span<const std::uint8_t> data){for(std::size_t i=0;i<data.size();){auto c=data[i++];if(c<0x80)continue;unsigned more=c>=0xc2&&c<=0xdf?1:c>=0xe0&&c<=0xef?2:c>=0xf0&&c<=0xf4?3:99;if(more>data.size()-i)throw std::runtime_error("UTF-8 文本无效");std::uint32_t value=c&((1<<(6-more))-1);for(unsigned n=0;n<more;n++){auto b=data[i++];if((b&0xc0)!=0x80)throw std::runtime_error("UTF-8 文本无效");value=(value<<6)|(b&63);}if((more==1&&value<0x80)||(more==2&&value<0x800)||(more==3&&value<0x10000)||value>0x10ffff||(value>=0xd800&&value<=0xdfff))throw std::runtime_error("UTF-8 文本无效");}}
}
class MessagePackReader {
public:
    explicit MessagePackReader(std::span<const std::uint8_t> input):source_(std::make_shared<Bytes>(input.begin(),input.end())){}
    std::size_t Position{};
    MessagePack Read(int depth=0){
        if(depth>48||++nodes_>1000000)throw std::runtime_error("MessagePack 嵌套或节点数量超限");
        MessagePack result;result.source_=source_;result.rawOffset_=Position;auto marker=Byte();
        auto text=[&](std::size_t length){if(length>4*1024*1024)throw std::runtime_error("MessagePack 文本过长");auto data=Take(length);ValidateUtf8(data);result.ScalarValue=std::string(reinterpret_cast<const char*>(data.data()),data.size());};
        auto items=[&](std::size_t count,MessagePack::Kind type){if(count>200000||count>source_->size()-Position)throw std::runtime_error("MessagePack 集合数量超限");result.Type=type;result.Children.reserve(count);for(std::size_t i=0;i<count;i++)result.Children.push_back(Read(depth+1));};
        auto binary=[&](std::size_t length,bool extension){result.Type=extension?MessagePack::Kind::Extension:MessagePack::Kind::Binary;if(extension)result.ScalarValue=static_cast<std::int64_t>(static_cast<std::int8_t>(Byte()));result.payloadOffset_=Position;result.payloadSize_=length;Take(length);};
        if(marker<=0x7f)result.ScalarValue=static_cast<std::int64_t>(marker);
        else if(marker>=0xe0)result.ScalarValue=static_cast<std::int64_t>(static_cast<std::int8_t>(marker));
        else if(marker>=0xa0&&marker<=0xbf)text(marker&31);
        else if(marker>=0x90&&marker<=0x9f)items(marker&15,MessagePack::Kind::Array);
        else if(marker>=0x80&&marker<=0x8f)items((marker&15)*2,MessagePack::Kind::Map);
        else switch(marker){
        case 0xc0:break;case 0xc2:result.ScalarValue=false;break;case 0xc3:result.ScalarValue=true;break;
        case 0xcc:result.ScalarValue=static_cast<std::int64_t>(Number(1));break;case 0xcd:result.ScalarValue=static_cast<std::int64_t>(Number(2));break;case 0xce:result.ScalarValue=static_cast<std::int64_t>(Number(4));break;case 0xcf:result.ScalarValue=Number(8);break;
        case 0xd0:result.ScalarValue=static_cast<std::int64_t>(static_cast<std::int8_t>(Number(1)));break;case 0xd1:result.ScalarValue=static_cast<std::int64_t>(static_cast<std::int16_t>(Number(2)));break;case 0xd2:result.ScalarValue=static_cast<std::int64_t>(static_cast<std::int32_t>(Number(4)));break;case 0xd3:result.ScalarValue=static_cast<std::int64_t>(Number(8));break;
        case 0xca:result.ScalarValue=std::bit_cast<float>(static_cast<std::uint32_t>(Number(4)));break;case 0xcb:result.ScalarValue=std::bit_cast<double>(Number(8));break;
        case 0xd9:text(Length(1));break;case 0xda:text(Length(2));break;case 0xdb:text(Length(4));break;
        case 0xc4:binary(Length(1),false);break;case 0xc5:binary(Length(2),false);break;case 0xc6:binary(Length(4),false);break;
        case 0xdc:items(Length(2),MessagePack::Kind::Array);break;case 0xdd:items(Length(4),MessagePack::Kind::Array);break;
        case 0xde:items(Length(2)*2,MessagePack::Kind::Map);break;case 0xdf:items(Length(4)*2,MessagePack::Kind::Map);break;
        case 0xd4:binary(1,true);break;case 0xd5:binary(2,true);break;case 0xd6:binary(4,true);break;case 0xd7:binary(8,true);break;case 0xd8:binary(16,true);break;
        case 0xc7:binary(Length(1),true);break;case 0xc8:binary(Length(2),true);break;case 0xc9:binary(Length(4),true);break;
        default:throw std::runtime_error("MessagePack 标记无效");}
        result.rawSize_=Position-result.rawOffset_;return result;
    }
private:
    std::shared_ptr<Bytes> source_;std::size_t nodes_{};
    std::uint8_t Byte(){if(Position==source_->size())throw std::runtime_error("MessagePack 数据截断");return (*source_)[Position++];}
    std::uint64_t Number(int n){std::uint64_t value=0;while(n--)value=(value<<8)|Byte();return value;}
    std::size_t Length(int n){auto value=Number(n);if(value>INT32_MAX)throw std::runtime_error("MessagePack 长度超限");return static_cast<std::size_t>(value);}
    std::span<const std::uint8_t> Take(std::size_t n){if(n>source_->size()-Position)throw std::runtime_error("MessagePack 数据越界");auto bytes=std::span(*source_).subspan(Position,n);Position+=n;return bytes;}
};
MessagePack MessagePack::Parse(std::span<const std::uint8_t> bytes){MessagePackReader reader(bytes);auto result=reader.Read();if(reader.Position!=bytes.size())throw std::runtime_error("MessagePack 后存在未识别的数据");return result;}
MessagePack MessagePack::Scalar(Value value){if(auto f=std::get_if<float>(&value);f&&!std::isfinite(*f))throw std::invalid_argument("参数必须是有限数值");if(auto d=std::get_if<double>(&value);d&&!std::isfinite(*d))throw std::invalid_argument("参数必须是有限数值");MessagePack result;result.ScalarValue=std::move(value);return result;}
MessagePack MessagePack::Array(std::vector<MessagePack> values){MessagePack result;result.Type=Kind::Array;result.Children=std::move(values);return result;}
MessagePack MessagePack::Map(std::vector<std::pair<Value,MessagePack>> values){MessagePack result;result.Type=Kind::Map;for(auto& [key,value]:values){result.Children.push_back(Scalar(std::move(key)));result.Children.push_back(std::move(value));}return result;}
MessagePack MessagePack::Binary(Bytes bytes){MessagePack result;result.Type=Kind::Binary;result.payloadSize_=bytes.size();result.source_=std::make_shared<Bytes>(std::move(bytes));return result;}
const MessagePack* MessagePack::Find(const Value& key)const{if(Type==Kind::Array){std::int64_t index=-1;if(auto p=std::get_if<std::int64_t>(&key))index=*p;else if(auto u=std::get_if<std::uint64_t>(&key);u&&*u<=INT64_MAX)index=static_cast<std::int64_t>(*u);return index>=0&&static_cast<std::size_t>(index)<Children.size()?&Children[static_cast<std::size_t>(index)]:nullptr;}if(Type!=Kind::Map)return nullptr;const MessagePack* found=nullptr;for(std::size_t i=0;i<Children.size();i+=2)if(EqualKey(Children[i].ScalarValue,key)){if(found)throw std::runtime_error("重复的参数键不能安全编辑");found=&Children[i+1];}return found;}
const MessagePack& MessagePack::Require(const Value& key)const{auto found=Find(key);if(!found)throw std::runtime_error("缺少参数");return *found;}
MessagePack MessagePack::Set(const Value& key,const MessagePack& value,bool allowAdd)const{auto old=Find(key);if(old&&old->ContentEquals(value))return *this;auto next=*this;next.rawSize_=0;if(Type==Kind::Array&&old){auto index=static_cast<std::size_t>(old-Children.data());next.Children[index]=value;return next;}if(Type==Kind::Map){for(std::size_t i=0;i<Children.size();i+=2)if(EqualKey(Children[i].ScalarValue,key)){next.Children[i+1]=value;return next;}if(allowAdd){next.Children.push_back(Scalar(key));next.Children.push_back(value);return next;}}throw std::runtime_error("无法编辑参数");}
MessagePack MessagePack::Remove(const Value& key)const{if(Type!=Kind::Map)throw std::runtime_error("预期参数字典");if(!Find(key))return *this;auto next=*this;next.rawSize_=0;next.Children.clear();for(std::size_t i=0;i<Children.size();i+=2)if(!EqualKey(Children[i].ScalarValue,key)){next.Children.push_back(Children[i]);next.Children.push_back(Children[i+1]);}return next;}
MessagePack MessagePack::WithScalar(Value value)const{if(Type!=Kind::Scalar)throw std::runtime_error("该参数不是可直接编辑的值");if(ScalarValue.index()!=0&&ScalarValue.index()!=value.index()){
    value=std::visit([&](const auto& input)->Value{using T=std::decay_t<decltype(input)>;if constexpr(std::is_arithmetic_v<T>){switch(ScalarValue.index()){case 2:{long double n=static_cast<long double>(input);if(!std::isfinite(n)||n<INT64_MIN||n>=9223372036854775808.0L||std::trunc(n)!=n)throw std::invalid_argument("整数参数越界");return static_cast<std::int64_t>(input);}case 3:{long double n=static_cast<long double>(input);if(!std::isfinite(n)||n<0||n>=18446744073709551616.0L||std::trunc(n)!=n)throw std::invalid_argument("整数参数越界");return static_cast<std::uint64_t>(input);}case 4:return static_cast<float>(input);case 5:return static_cast<double>(input);}}throw std::invalid_argument("编辑参数类型不匹配");},value);
    }if(value==ScalarValue)return *this;return Scalar(std::move(value));}
bool MessagePack::ContentEquals(const MessagePack& other)const{if(source_==other.source_&&rawSize_&&rawSize_==other.rawSize_&&rawOffset_==other.rawOffset_)return true;if(Type!=other.Type||ScalarValue!=other.ScalarValue||Children.size()!=other.Children.size())return false;if((Type==Kind::Binary||Type==Kind::Extension)&&!std::equal(Payload().begin(),Payload().end(),other.Payload().begin(),other.Payload().end()))return false;for(std::size_t i=0;i<Children.size();i++)if(!Children[i].ContentEquals(other.Children[i]))return false;return true;}
std::size_t MessagePack::Count()const{return Type==Kind::Map?Children.size()/2:Children.size();}
std::int64_t MessagePack::Integer()const{if(auto p=std::get_if<std::int64_t>(&ScalarValue))return *p;if(auto p=std::get_if<std::uint64_t>(&ScalarValue);p&&*p<=INT64_MAX)return static_cast<std::int64_t>(*p);throw std::runtime_error("预期整数参数");}
std::string MessagePack::Text()const{if(auto p=std::get_if<std::string>(&ScalarValue))return *p;throw std::runtime_error("预期文本参数");}
std::span<const std::uint8_t> MessagePack::Payload()const{return source_?std::span<const std::uint8_t>(*source_).subspan(payloadOffset_,payloadSize_):std::span<const std::uint8_t>();}
Bytes MessagePack::ToBytes()const{Bytes result;Write(result);return result;}
void MessagePack::Write(Bytes& output)const{
    if(rawSize_){output.insert(output.end(),source_->begin()+rawOffset_,source_->begin()+rawOffset_+rawSize_);return;}
    if(Type==Kind::Array||Type==Kind::Map){Container(output,Count(),Type==Kind::Array?0x90:0x80,Type==Kind::Array?0xdc:0xde,Type==Kind::Array?0xdd:0xdf);for(auto& child:Children)child.Write(output);return;}
    if(Type==Kind::Binary){Length(output,Payload().size(),0xc4,0xc5,0xc6);output.insert(output.end(),Payload().begin(),Payload().end());return;}
    if(Type==Kind::Extension)throw std::runtime_error("未知扩展参数不可重编码");
    std::visit([&](const auto& value){using T=std::decay_t<decltype(value)>;
        if constexpr(std::is_same_v<T,std::monostate>)output.push_back(0xc0);
        else if constexpr(std::is_same_v<T,bool>)output.push_back(value?0xc3:0xc2);
        else if constexpr(std::is_same_v<T,std::uint64_t>)Unsigned(output,value);
        else if constexpr(std::is_same_v<T,std::int64_t>){if(value>=0)Unsigned(output,static_cast<std::uint64_t>(value));else if(value>=-32)output.push_back(static_cast<std::uint8_t>(value));else{int width=value>=INT8_MIN?1:value>=INT16_MIN?2:value>=INT32_MIN?4:8;output.push_back(static_cast<std::uint8_t>(width==1?0xd0:width==2?0xd1:width==4?0xd2:0xd3));Number(output,static_cast<std::uint64_t>(value),width);}}
        else if constexpr(std::is_same_v<T,float>){output.push_back(0xca);Number(output,std::bit_cast<std::uint32_t>(value),4);}
        else if constexpr(std::is_same_v<T,double>){output.push_back(0xcb);Number(output,std::bit_cast<std::uint64_t>(value),8);}
        else{if(value.size()<32)output.push_back(static_cast<std::uint8_t>(0xa0|value.size()));else Length(output,value.size(),0xd9,0xda,0xdb);output.insert(output.end(),value.begin(),value.end());}
    },ScalarValue);
}
}
